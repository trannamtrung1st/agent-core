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
        if (_sessions.TryRemove(sessionId, out var session))
        {
            _sessionOwners.TryRemove(sessionId, out _);
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
        IReadOnlyList<string> origins,
        CancellationToken cancellationToken = default)
    {
        if (agentInstanceId == Guid.Empty)
        {
            throw new ArgumentException("Agent instance is required.", nameof(agentInstanceId));
        }

        var gate = _contextUse.GetOrAdd(agentInstanceId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        _unattendedLeases[agentInstanceId] = origins.ToArray();
        return new UnattendedLease(this, agentInstanceId, gate);
    }

    public void AdoptUnattendedFlow(Guid agentInstanceId)
    {
        if (agentInstanceId != Guid.Empty && _unattendedLeases.ContainsKey(agentInstanceId))
        {
            _unattendedOwner.Value = agentInstanceId;
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
            || !_unattendedLeases.ContainsKey(agentInstanceId)
            || _unattendedOwner.Value == agentInstanceId)
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

    private string[]? LeaseOrigins(Guid sessionId)
    {
        if (_sessionOwners.TryGetValue(sessionId, out var owner)
            && owner is Guid agentInstanceId
            && _unattendedLeases.TryGetValue(agentInstanceId, out var origins))
        {
            return origins.Length == 0 ? null : origins;
        }

        if (_unattendedOwner.Value is Guid current && _unattendedLeases.TryGetValue(current, out var leased))
        {
            return leased.Length == 0 ? null : leased;
        }

        return null;
    }

    private string[]? LeaseOrigins(SessionBrowser session) =>
        session.AgentInstanceId is Guid agentInstanceId && _unattendedLeases.TryGetValue(agentInstanceId, out var origins)
            ? (origins.Length == 0 ? null : origins)
            : null;

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
        var environmentOptions = ContextOptions(_playwright!);
        var context = await browser.NewContextAsync(environmentOptions)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        await context.AddInitScriptAsync(BrowserPageSettle.InitScript).WaitAsync(cancellationToken).ConfigureAwait(false);
        var page = await context.NewPageAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        page.SetDefaultTimeout(TimeoutMs());
        page.SetDefaultNavigationTimeout(TimeoutMs());
        var session = new SessionBrowser(context, page) { Environment = DescribeEnvironment(environmentOptions) };
        RememberPage(session, page);
        context.Close += (_, _) => ForgetClosed(session);
        context.Page += (_, opened) => OnContextPage(session, opened);
        await context.RouteAsync("**/*", route => RouteAsync(session, route)).ConfigureAwait(false);
        await context.RouteWebSocketAsync("**/*", socket => RouteWebSocket(session, socket)).ConfigureAwait(false);
        if (!_sessions.TryAdd(sessionId, session))
        {
            await CloseQuietlyAsync(context).ConfigureAwait(false);
            return _sessions[sessionId];
        }

        return session;
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

                playwright = await Playwright.CreateAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
                var environmentOptions = ContextOptions(playwright);
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
                    Args = ["--disable-popup-blocking"]
                };
                if (!string.IsNullOrWhiteSpace(_options.Channel))
                {
                    options.Channel = _options.Channel;
                }

                IBrowserContext context;
                try
                {
                    context = await playwright.Chromium.LaunchPersistentContextAsync(directory, options)
                        .WaitAsync(cancellationToken)
                        .ConfigureAwait(false);
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

                var page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync().ConfigureAwait(false);
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
                RememberPage(session, page);
                context.Close += (_, _) => ForgetClosed(session);
                context.Page += (_, opened) => OnContextPage(session, opened);
                await context.RouteAsync("**/*", route => RouteAsync(session, route)).ConfigureAwait(false);
                await context.RouteWebSocketAsync("**/*", socket => RouteWebSocket(session, socket)).ConfigureAwait(false);
                _persistent[agentInstanceId] = session;
                lease = null;
                playwright = null;
                return session;
            }
            finally
            {
                playwright?.Dispose();
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

    private sealed class UnattendedLease(NativePlaywrightBrowser owner, Guid agentInstanceId, SemaphoreSlim gate)
        : IAsyncDisposable
    {
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return ValueTask.CompletedTask;
            }

            owner._unattendedLeases.TryRemove(agentInstanceId, out _);
            if (owner._unattendedOwner.Value == agentInstanceId)
            {
                owner._unattendedOwner.Value = null;
            }

            gate.Release();
            return ValueTask.CompletedTask;
        }
    }
}
