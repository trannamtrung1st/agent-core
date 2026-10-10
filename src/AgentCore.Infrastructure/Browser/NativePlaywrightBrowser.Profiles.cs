using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;
using SkiaSharp;

namespace AgentCore.Infrastructure.Browser;

public sealed partial class NativePlaywrightBrowser
{
    private async ValueTask<BrowserCloseResult> CloseAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var interactive = await EnterInteractiveAsync(sessionId, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _stopped) == 1 || !IsRuntimeReady)
        {
            return new BrowserCloseResult("provider_unavailable");
        }

        if (_options.ResolveProfile() == BrowserProfileMode.PersistentAgent
            && _sessionOwners.TryGetValue(sessionId, out var owner)
            && owner is Guid agentInstanceId)
        {
            return await ClosePersistentAsync(agentInstanceId, cancellationToken).ConfigureAwait(false);
        }

        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            return new BrowserCloseResult("already_closed");
        }

        if (session.Persistent && session.AgentInstanceId is Guid persistentAgent)
        {
            return await ClosePersistentAsync(persistentAgent, cancellationToken).ConfigureAwait(false);
        }

        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CloseExplicitlyAsync(session, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public async ValueTask ResetPersistentProfileAsync(Guid agentInstanceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsRuntimeReady && Volatile.Read(ref _stopped) == 0)
        {
            var closed = await ClosePersistentAsync(agentInstanceId, cancellationToken).ConfigureAwait(false);
            if (closed.Status is not ("closed" or "already_closed")) throw new IOException("Browser profile closure was not confirmed.");
        }

        var directory = ProfileDirectory(agentInstanceId);
        var lockFile = directory + ".lock";
        try
        {
            if (Directory.Exists(directory))
            {
                var info = new DirectoryInfo(directory);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new IOException();
                }

                info.Delete(recursive: true);
            }

            if (File.Exists(lockFile))
            {
                File.Delete(lockFile);
            }
        }
        catch (IOException)
        {
            _logger.LogWarning("Browser profile reset failed.");
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            _logger.LogWarning("Browser profile reset failed.");
            throw;
        }

        _logger.LogInformation("Browser profile reset.");
    }

    private async Task<BrowserCloseResult> ClosePersistentAsync(Guid agentInstanceId, CancellationToken cancellationToken)
    {
        var gate = _profileGates.GetOrAdd(agentInstanceId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_persistent.TryGetValue(agentInstanceId, out var session))
            {
                return new BrowserCloseResult("already_closed");
            }

            await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await CloseExplicitlyAsync(session, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                session.Gate.Release();
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<BrowserCloseResult> CloseExplicitlyAsync(SessionBrowser session, CancellationToken ct)
    {
        if (Volatile.Read(ref session.RuntimeClosed) == 1) return new("already_closed");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(TimeoutMs()));
        try
        {
            var closing = ExplicitCloseProbe is { } close ? close(session.Context) : session.Context.CloseAsync();
            _ = closing.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
            await closing.WaitAsync(deadline.Token).ConfigureAwait(false);
            // The native Close event, rather than an intent flag, confirms actual context closure.
            if (Volatile.Read(ref session.RuntimeClosed) != 1) return new("close_uncertain");
            DropClosed(session);
            return new("closed");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return new(Volatile.Read(ref session.RuntimeClosed) == 1 ? "closed" : "close_uncertain"); }
        catch (PlaywrightException)
        { return new(Volatile.Read(ref session.RuntimeClosed) == 1 ? "closed" : "close_failed"); }
    }

    private void ForgetClosed(SessionBrowser session)
    {
        if (Interlocked.Exchange(ref session.RuntimeClosed, 1) == 1)
        {
            return;
        }

        if (session.Persistent && session.AgentInstanceId is Guid agentInstanceId)
        {
            _persistent.TryRemove(agentInstanceId, out _);
            try
            {
                session.ProfileLease?.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }

            if (session.PlaywrightDriver is not null)
            {
                _retiredDrivers.Add(session.PlaywrightDriver);
            }
        }

        foreach (var sessionId in _sessions.Where(pair => ReferenceEquals(pair.Value, session)).Select(pair => pair.Key).ToArray())
        {
            _sessions.TryRemove(sessionId, out _);
        }
    }

    private void DropClosed(SessionBrowser session)
    {
        if (session.Persistent && session.AgentInstanceId is Guid agentInstanceId)
        {
            _persistent.TryRemove(agentInstanceId, out _);
            try
            {
                session.ProfileLease?.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }

            DisposeQuietly(session.PlaywrightDriver);
        }

        foreach (var sessionId in _sessions.Where(pair => ReferenceEquals(pair.Value, session)).Select(pair => pair.Key).ToArray())
        {
            _sessions.TryRemove(sessionId, out _);
        }
    }

    private void DrainRetiredDrivers()
    {
        while (_retiredDrivers.TryTake(out var driver))
        {
            DisposeQuietly(driver);
        }
    }

    public async ValueTask ReleaseAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var interactive = await EnterInteractiveAsync(sessionId, cancellationToken).ConfigureAwait(false);
        _sessionOwners.TryRemove(sessionId, out _);
        if (_sessions.TryRemove(sessionId, out var session))
        {
            if (!session.Persistent)
            {
                await CloseQuietlyAsync(session.Context).ConfigureAwait(false);
            }
        }
    }

    public void BindSession(Guid sessionId, Guid? agentInstanceId) =>
        _sessionOwners[sessionId] = agentInstanceId;

    public async ValueTask<IAsyncDisposable> EnterUnattendedAsync(
        Guid agentInstanceId,
        IReadOnlyList<string>? origins,
        CancellationToken cancellationToken = default)
    {
        if (agentInstanceId == Guid.Empty)
        {
            throw new ArgumentException("Agent instance is required.", nameof(agentInstanceId));
        }

        var gate = _contextUse.GetOrAdd(agentInstanceId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var lease = new UnattendedLease(this, agentInstanceId, gate, origins?.ToArray());
        _unattendedLeases[agentInstanceId] = lease;
        ProxyFor(Guid.Empty, agentInstanceId).FenceConnections();
        return lease;
    }

    public void AdoptUnattendedFlow(Guid agentInstanceId)
    {
        if (agentInstanceId != Guid.Empty && _unattendedLeases.TryGetValue(agentInstanceId, out var lease))
        {
            _unattendedOwner.Value = lease;
        }
    }

    internal void ExpectInteractive(Guid agentInstanceId) =>
        _interactiveWaiters.TryAdd(
            agentInstanceId,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    internal Task InteractiveEntered(Guid agentInstanceId) =>
        _interactiveWaiters[agentInstanceId].Task;

    private async ValueTask<IAsyncDisposable> EnterInteractiveAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (!_sessionOwners.TryGetValue(sessionId, out var owner)
            || owner is not Guid agentInstanceId
            || agentInstanceId == Guid.Empty
            || _unattendedOwner.Value is { } flow && _unattendedLeases.TryGetValue(agentInstanceId, out var current)
                && ReferenceEquals(flow, current))
        {
            return NoopHold.Instance;
        }

        var gate = _contextUse.GetOrAdd(agentInstanceId, static _ => new SemaphoreSlim(1, 1));
        if (_interactiveWaiters.TryGetValue(agentInstanceId, out var waiting))
        {
            waiting.TrySetResult();
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new InteractiveHold(gate);
    }

    // Null inherits the host policy; an empty restriction denies every origin.
    private string[]? LeaseOrigins(Guid sessionId) =>
        _sessionOwners.TryGetValue(sessionId, out var owner) && owner is Guid agent
            && _unattendedLeases.TryGetValue(agent, out var lease) ? lease.Origins : null;

    private string[]? LeaseOrigins(SessionBrowser session) =>
        session.AgentInstanceId is Guid agent && _unattendedLeases.TryGetValue(agent, out var lease) ? lease.Origins : null;

    private BrowserDestinationProxy ProxyFor(Guid sessionId, Guid? agentInstanceId)
    {
        if (agentInstanceId is null) return _destinationProxy!;
        var key = (Agent: true, Id: agentInstanceId.Value);
        return _ownerProxies.GetOrAdd(key, _ => new Lazy<BrowserDestinationProxy>(() =>
        {
            var proxy = new BrowserDestinationProxy(_policy, uri =>
                agentInstanceId is not Guid agent || !_unattendedLeases.TryGetValue(agent, out var lease)
                    || lease.Origins is null || BrowserTargetPolicy.EvaluateDestination(uri.AbsoluteUri, lease.Origins).Allowed);
            // One resolver policy, including deterministic test injection, for every native context.
            proxy.ResolveAsync = (host, ct) => _destinationProxy!.ResolveAsync(host, ct);
            proxy.Start();
            return proxy;
        })).Value;
    }

    private async Task<SessionBrowser> EnsureSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (_sessions.TryGetValue(sessionId, out var existing))
        {
            return existing;
        }

        if (_options.ResolveProfile() == BrowserProfileMode.PersistentAgent
            && _sessionOwners.TryGetValue(sessionId, out var owner)
            && owner is Guid agentInstanceId)
        {
            var persistent = await EnsurePersistentAsync(agentInstanceId, cancellationToken).ConfigureAwait(false);
            if (!_sessions.TryAdd(sessionId, persistent))
            {
                return _sessions[sessionId];
            }

            return persistent;
        }

        var browser = await EnsureBrowserAsync(cancellationToken).ConfigureAwait(false);
        var boundOwner = _sessionOwners.TryGetValue(sessionId, out var sessionOwner) ? sessionOwner : null;
        var environmentOptions = ContextOptions(_playwright!, ProxyFor(sessionId, boundOwner).Server);
        var context = await OwnContextAsync(browser.NewContextAsync(environmentOptions), cancellationToken);
        try
        {
            await InitializeStageAsync("initScript", context, cancellationToken);
            await context.AddInitScriptAsync(BrowserPageSettle.InitScript).WaitAsync(cancellationToken);
            await InitializeStageAsync("page", context, cancellationToken);
            var page = await context.NewPageAsync().WaitAsync(cancellationToken);
            page.SetDefaultTimeout(TimeoutMs());
            page.SetDefaultNavigationTimeout(TimeoutMs());
            var session = new SessionBrowser(context, page)
            {
                Environment = DescribeEnvironment(environmentOptions),
                AgentInstanceId = _sessionOwners.TryGetValue(sessionId, out var ownerId) ? ownerId : null
            };
            await InstallRoutesAsync(session, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!_sessions.TryAdd(sessionId, session))
            {
                await CloseQuietlyAsync(context);
                return _sessions[sessionId];
            }
            return session;
        }
        catch
        {
            await CloseQuietlyAsync(context);
            throw;
        }
    }

    private async Task InitializeStageAsync(string stage, IBrowserContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (InitializationProbe is { } probe) await probe(stage, context).WaitAsync(ct);
    }

    private async Task InstallRoutesAsync(SessionBrowser session, CancellationToken ct)
    {
        var context = session.Context;
        RememberPage(session, session.Page);
        await InitializeStageAsync("routes", context, ct);
        await context.RouteAsync("**/*", route => RouteAsync(session, route)).WaitAsync(ct);
        await context.RouteWebSocketAsync("**/*", socket => RouteWebSocket(session, socket)).WaitAsync(ct);
        // Publish lifecycle hooks only after setup; unpublished contexts cannot evict live entries.
        context.Close += (_, _) => ForgetClosed(session);
        context.Page += (_, opened) => OnContextPage(session, opened);
    }

    private static async Task<IPlaywright> OwnDriverAsync(Task<IPlaywright> creation, CancellationToken ct)
    {
        try { return await creation.WaitAsync(ct); }
        catch (OperationCanceledException)
        {
            _ = creation.ContinueWith(result =>
            {
                if (result.IsCompletedSuccessfully) DisposeQuietly(result.Result);
                else _ = result.Exception;
            }, TaskScheduler.Default);
            throw;
        }
    }

    private static async Task<IBrowserContext> OwnContextAsync(Task<IBrowserContext> creation, CancellationToken ct)
    {
        try { return await creation.WaitAsync(ct); }
        catch (OperationCanceledException)
        {
            _ = creation.ContinueWith(async result =>
            {
                if (result.IsCompletedSuccessfully) await CloseQuietlyAsync(result.Result);
                else _ = result.Exception;
            }, TaskScheduler.Default).Unwrap();
            throw;
        }
    }

    private async Task<SessionBrowser> EnsurePersistentAsync(Guid agentInstanceId, CancellationToken cancellationToken)
    {
        if (_persistent.TryGetValue(agentInstanceId, out var existing))
        {
            return existing;
        }

        var gate = _profileGates.GetOrAdd(agentInstanceId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_persistent.TryGetValue(agentInstanceId, out existing))
            {
                return existing;
            }

            DrainRetiredDrivers();

            string directory;
            try
            {
                directory = ProfileDirectory(agentInstanceId);
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Browser profile is unavailable.");
                throw new BrowserProfileException("profile_unavailable");
            }

            FileStream? lease = null;
            IPlaywright? playwright = null;
            IBrowserContext? context = null;
            try
            {
                try
                {
                    lease = new FileStream(
                        directory + ".lock",
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning("Browser profile is unavailable.");
                    throw new BrowserProfileException("profile_busy");
                }

                playwright = await OwnDriverAsync(Playwright.CreateAsync(), cancellationToken);
                var environmentOptions = ContextOptions(playwright, ProxyFor(Guid.Empty, agentInstanceId).Server);
                var options = new BrowserTypeLaunchPersistentContextOptions
                {
                    Headless = _options.Headless,
                    AcceptDownloads = true,
                    ViewportSize = environmentOptions.ViewportSize,
                    ScreenSize = environmentOptions.ScreenSize,
                    Locale = environmentOptions.Locale,
                    TimezoneId = environmentOptions.TimezoneId,
                    IsMobile = environmentOptions.IsMobile,
                    HasTouch = environmentOptions.HasTouch,
                    DeviceScaleFactor = environmentOptions.DeviceScaleFactor,
                    UserAgent = environmentOptions.UserAgent,
                    ServiceWorkers = ServiceWorkerPolicy.Block,
                    Args = ["--disable-popup-blocking", "--proxy-bypass-list=<-loopback>", "--disable-quic", "--force-webrtc-ip-handling-policy=disable_non_proxied_udp"],
                    Proxy = new() { Server = environmentOptions.Proxy!.Server }
                };
                if (!string.IsNullOrWhiteSpace(_options.Channel))
                {
                    options.Channel = _options.Channel;
                }

                try
                {
                    context = await OwnContextAsync(playwright.Chromium.LaunchPersistentContextAsync(directory, options), cancellationToken);
                    await InitializeStageAsync("persistent", context, cancellationToken);
                    await InitializeStageAsync("initScript", context, cancellationToken);
                    await context.AddInitScriptAsync(BrowserPageSettle.InitScript).WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (PlaywrightException ex)
                {
                    _logger.LogWarning("Browser profile is unavailable.");
                    var busy = ex.Message.Contains("lock", StringComparison.OrdinalIgnoreCase)
                        || ex.Message.Contains("already", StringComparison.OrdinalIgnoreCase)
                        || ex.Message.Contains("in use", StringComparison.OrdinalIgnoreCase)
                        || ex.Message.Contains("ProcessSingleton", StringComparison.OrdinalIgnoreCase);
                    throw new BrowserProfileException(busy ? "profile_busy" : "profile_unavailable");
                }

                await InitializeStageAsync("page", context, cancellationToken);
                var page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync().WaitAsync(cancellationToken);
                page.SetDefaultTimeout(TimeoutMs());
                page.SetDefaultNavigationTimeout(TimeoutMs());
                var session = new SessionBrowser(context, page)
                {
                    Persistent = true,
                    ProfileLease = lease,
                    PlaywrightDriver = playwright,
                    AgentInstanceId = agentInstanceId,
                    Environment = DescribeEnvironment(environmentOptions)
                };
                await InstallRoutesAsync(session, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                _persistent[agentInstanceId] = session;
                lease = null;
                playwright = null;
                context = null;
                return session;
            }
            finally
            {
                if (context is not null) await CloseQuietlyAsync(context);
                DisposeQuietly(playwright);
                lease?.Dispose();
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private string ProfileDirectory(Guid agentInstanceId)
    {
        var root = string.IsNullOrWhiteSpace(_options.ProfileRoot) ? "data/browser-profiles" : _options.ProfileRoot;
        var name = agentInstanceId.ToString("D");
        var rootFull = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(rootFull, name));
        if (!full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !full.Equals(rootFull, StringComparison.Ordinal))
        {
            throw new IOException();
        }

        return full;
    }

    private sealed class BrowserProfileException(string code) : Exception
    {
        public string Code { get; } = code;
    }

    private sealed class NoopHold : IAsyncDisposable
    {
        public static NoopHold Instance { get; } = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class InteractiveHold(SemaphoreSlim gate) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            gate.Release();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class UnattendedLease(NativePlaywrightBrowser owner, Guid agentInstanceId, SemaphoreSlim gate, string[]? origins)
        : IAsyncDisposable
    {
        public string[]? Origins { get; } = origins;
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return ValueTask.CompletedTask;
            }

            if (owner._ownerProxies.TryGetValue((true, agentInstanceId), out var proxy) && proxy.IsValueCreated)
                proxy.Value.FenceConnections();
            owner._unattendedLeases.TryRemove(agentInstanceId, out _);
            if (ReferenceEquals(owner._unattendedOwner.Value, this)) owner._unattendedOwner.Value = null;

            gate.Release();
            return ValueTask.CompletedTask;
        }
    }
}
