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

public sealed partial class NativePlaywrightBrowser : AgentCore.Application.Ports.IBrowser, IBrowserPasswordSink, IBrowserLease, IBrowserProfileBinding, IBrowserContextUse, IBrowserRuntimeReadiness, IBrowserProfileReset, IHostedService
{
    private readonly BrowserOptions _options;
    private readonly ILogger _logger;
    private readonly LoopbackBrowserFixtureHost _fixture;
    private readonly Func<CancellationToken, Task<bool>>? _chromiumProbe;
    private readonly SemaphoreSlim _launch = new(1, 1);
    private readonly ConcurrentDictionary<Guid, SessionBrowser> _sessions = new();
    private readonly ConcurrentDictionary<Guid, Guid?> _sessionOwners = new();
    private readonly ConcurrentDictionary<Guid, SessionBrowser> _persistent = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _profileGates = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _contextUse = new();
    private readonly ConcurrentDictionary<Guid, string[]> _unattendedLeases = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _interactiveWaiters = new();
    private readonly AsyncLocal<Guid?> _unattendedOwner = new();
    private readonly ConcurrentBag<IPlaywright> _retiredDrivers = [];
    private readonly TimeProvider _time;
    private IPlaywright? _playwright;
    private Microsoft.Playwright.IBrowser? _browser;
    private BrowserHostPolicy _policy;
    private int _runtimeReady;
    private int _stopped;

    internal Func<Exception?>? CaptureProbe { get; set; }
    internal Func<IPage, int, int, Task>? ResizeProbe { get; set; }
    internal Func<IPage, Task>? ActivateTabProbe { get; set; }
    internal Func<IDialog, Task>? DialogResolutionProbe { get; set; }
    internal Func<IBrowserContext, Task>? ExplicitCloseProbe { get; set; }
    internal Func<IPage, Task>? DeniedPopupCloseProbe { get; set; }
    internal Action? PopupCleanupWaitProbe { get; set; }
    internal Action? ActionStartedProbe { get; set; }

    public NativePlaywrightBrowser(
        BrowserOptions options,
        ILoggerFactory? loggerFactory,
        Func<CancellationToken, Task<bool>>? chromiumProbe = null, TimeProvider? timeProvider = null)
    {
        _options = options;
        _time = timeProvider ?? TimeProvider.System;
        _logger = loggerFactory?.CreateLogger<NativePlaywrightBrowser>()
            ?? NullLogger<NativePlaywrightBrowser>.Instance;
        _fixture = new LoopbackBrowserFixtureHost(_logger);
        _chromiumProbe = chromiumProbe;
        _policy = options.ToHostPolicy();
    }

    internal LoopbackBrowserFixtureHost Fixture => _fixture;

    internal bool? LaunchedHeadless { get; private set; }

    internal TimeSpan OperationTimeout { get; set; } = TimeSpan.FromSeconds(25);

    public bool IsRuntimeReady => Volatile.Read(ref _runtimeReady) == 1;

    public bool IsAvailable =>
        _options.Enabled && IsRuntimeReady && Volatile.Read(ref _stopped) == 0;

    public BrowserHostPolicy HostPolicy => _policy;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _policy = _options.ToHostPolicy();
            return;
        }

        string? liveOrigin = null;
        int? livePort = null;
        if (_options.FixtureEnabled)
        {
            await _fixture.StartAsync(_options.FixturePort, cancellationToken).ConfigureAwait(false);
            if (_fixture.IsAvailable && _fixture.Origin is not null && _fixture.Port is not null)
            {
                liveOrigin = _fixture.Origin;
                livePort = _fixture.Port;
            }
        }

        var navigation = Retarget(_options.ResolveNavigation(), liveOrigin, livePort);
        if (liveOrigin is not null && navigation.Length == 0)
        {
            navigation = [liveOrigin];
        }

        var interaction = Retarget(_options.ResolveInteraction(navigation), liveOrigin, livePort);
        _policy = _options.ToHostPolicy() with
        {
            NavigationOrigins = navigation,
            InteractionOrigins = interaction,
            ResourceOrigins = _options.ResourceOrigins ?? []
        };
        var ready = _chromiumProbe is null
            ? await PlaywrightChromiumReadiness.InstalledAsync(_options.Channel, cancellationToken).ConfigureAwait(false)
            : await _chromiumProbe(cancellationToken).ConfigureAwait(false);
        if (!ready)
        {
            _logger.LogWarning("Browser provider is unavailable.");
            return;
        }

        Volatile.Write(ref _runtimeReady, 1);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 1)
        {
            return;
        }

        foreach (var session in _sessions.Values.Distinct())
        {
            if (!session.Persistent)
            {
                await CloseQuietlyAsync(session.Context).ConfigureAwait(false);
            }
        }

        foreach (var session in _persistent.Values)
        {
            Interlocked.Exchange(ref session.RuntimeClosed, 1);
            await CloseQuietlyAsync(session.Context).ConfigureAwait(false);
            session.ProfileLease?.Dispose();
            DisposeQuietly(session.PlaywrightDriver);
        }

        _sessions.Clear();
        _persistent.Clear();
        _sessionOwners.Clear();
        if (_browser is not null)
        {
            try
            {
                await _browser.CloseAsync().ConfigureAwait(false);
            }
            catch (PlaywrightException)
            {
            }
        }

        _playwright?.Dispose();
        _browser = null;
        _playwright = null;
        DrainRetiredDrivers();
        await _fixture.DisposeAsync().ConfigureAwait(false);
    }

    private async ValueTask<BrowserResult> NavigateAsync(
        BrowserRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var interactive = await EnterInteractiveAsync(request.SessionId, cancellationToken).ConfigureAwait(false);
        if (!IsAvailable)
        {
            return Unavailable();
        }

        var operation = ((BrowserNavigate)request.Command).Operation is "back" or "forward" or "reload" ? ((BrowserNavigate)request.Command).Operation : "goto";
        if (operation == "goto")
        {
            if (((BrowserNavigate)request.Command).Url is null)
            {
                return Result("invalid");
            }

            var leasedOrigins = LeaseOrigins(request.SessionId);
            var decision = leasedOrigins is null
                ? BrowserTargetPolicy.EvaluateDestination(
                    ((BrowserNavigate)request.Command).Url,
                    _policy.NavigationOrigins,
                    _policy.PolicyMode)
                : BrowserTargetPolicy.EvaluateDestination(
                    ((BrowserNavigate)request.Command).Url,
                    leasedOrigins,
                    BrowserPolicyMode.Restricted);
            if (!decision.Allowed)
            {
                return Result(decision.Code ?? "target_denied");
            }
        }

        SessionBrowser? session = null;
        var entered = false;
        try
        {
            session = await EnsureSessionAsync(request.SessionId, cancellationToken).ConfigureAwait(false);
            await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            session.DeniedNavigation = false;
            session.PopupCode = null;
            session.TimedOut = false;
            var call = BeginCall(session);
            using var registration = cancellationToken.Register(() => CancelCall(session, call));
            try
            {
                if (operation == "back")
                {
                    await session.Page.GoBackAsync(new PageGoBackOptions
                        {
                            Timeout = TimeoutMs(),
                            WaitUntil = WaitUntilState.DOMContentLoaded
                        })
                        .WaitAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                else if (operation == "forward")
                {
                    await session.Page.GoForwardAsync(new PageGoForwardOptions
                        {
                            Timeout = TimeoutMs(),
                            WaitUntil = WaitUntilState.DOMContentLoaded
                        })
                        .WaitAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                else if (operation == "reload")
                {
                    await session.Page.ReloadAsync(new PageReloadOptions
                        {
                            Timeout = TimeoutMs(),
                            WaitUntil = WaitUntilState.DOMContentLoaded
                        })
                        .WaitAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await session.Page.GotoAsync(
                            ((BrowserNavigate)request.Command).Url!,
                            new PageGotoOptions
                            {
                                Timeout = TimeoutMs(),
                                WaitUntil = WaitUntilState.DOMContentLoaded
                            })
                        .WaitAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (PlaywrightException) when (session.DeniedNavigation || session.PopupCode is not null || session.TimedOut)
            {
            }
            catch (PlaywrightException ex) when (IsDownloadStart(ex))
            {
            }
            catch (PlaywrightException ex) when (CanKeepInterruptedNavigation(session, ex))
            {
            }

            if (session.TimedOut)
            {
                LogBrowserFailure("navigate", "interaction", "timeout");
                return Result("timeout");
            }

            if (session.PopupCode is not null)
            {
                return Result(session.PopupCode);
            }

            if (session.DeniedNavigation || !IsAllowed(session, session.Page.Url))
            {
                await RestoreAllowedPageAsync(session, cancellationToken).ConfigureAwait(false);
                return Result("target_denied");
            }

            AdvanceGeneration(session);
            session.LastAllowedUrl = session.Page.Url;
            return await CaptureWithRetryAsync(
                session,
                request.SessionId,
                "navigate",
                BrowserSnapshotSettle.Automatic,
                timeoutMs: null,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (session is not null)
            {
                await FinishCancellationAsync(session).ConfigureAwait(false);
            }

            throw;
        }
        catch (Exception ex) when (IsCancel(ex, cancellationToken))
        {
            if (session is not null)
            {
                await FinishCancellationAsync(session).ConfigureAwait(false);
            }

            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception ex) when (IsTimeout(ex))
        {
            LogBrowserFailure("navigate", "interaction", "timeout");
            return Result("timeout");
        }
        catch (BrowserProfileException ex)
        {
            return Result(ex.Code);
        }
        catch (BrowserLaunchException)
        {
            LogBrowserFailure("navigate", "launch", "browserDisconnected");
            return Unavailable();
        }
        catch (PlaywrightException ex)
        {
            return await FailAsync(session, "navigate", "interaction", ex).ConfigureAwait(false);
        }
        finally
        {
            if (session is not null)
            {
                await SettlePopupsAsync(session).ConfigureAwait(false);
            }

            if (entered)
            {
                session?.Gate.Release();
            }
        }
    }

    internal IBrowserContext? ContextFor(Guid sessionId) =>
        _sessions.TryGetValue(sessionId, out var session) ? session.Context : null;

    private async Task<Microsoft.Playwright.IBrowser> EnsureBrowserAsync(CancellationToken cancellationToken)
    {
        if (_browser is not null)
        {
            return _browser;
        }

        await _launch.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_browser is not null)
            {
                return _browser;
            }

            try
            {
                _playwright = await Playwright.CreateAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
                var options = new BrowserTypeLaunchOptions
                {
                    Headless = _options.Headless,
                    Args = ["--disable-popup-blocking"]
                };
                if (!string.IsNullOrWhiteSpace(_options.Channel)) options.Channel = _options.Channel;
                _browser = await _playwright.Chromium.LaunchAsync(options).WaitAsync(cancellationToken).ConfigureAwait(false);
                LaunchedHeadless = options.Headless;
                return _browser;
            }
            catch (Exception ex) when (ex is PlaywrightException && !cancellationToken.IsCancellationRequested)
            {
                _playwright?.Dispose();
                _playwright = null;
                _browser = null;
                _logger.LogWarning("Browser provider is unavailable.");
                throw new BrowserLaunchException();
            }
        }
        finally
        {
            _launch.Release();
        }
    }

    private Task<BrowserSnapshot> CaptureAsync(SessionBrowser session, Guid sessionId, CancellationToken ct) =>
        ObserveAsync(session, sessionId, session.Page.Locator("body"), 32, null, ct);

    private async Task<BrowserSnapshot> CaptureMarkedAsync(
        SessionBrowser session,
        Guid sessionId,
        bool? settled,
        CancellationToken cancellationToken)
    {
        var observation = await CaptureAsync(session, sessionId, cancellationToken).ConfigureAwait(false);
        return settled is bool value ? observation with { Settled = value } : observation;
    }

    private const string ClassifyInterventionScript = """
        () => {
          const isVisible = (el) => {
            if (!(el instanceof Element) || !el.isConnected) return false;
            if (el.closest("template")) return false;
            if (el.hasAttribute("disabled")) return false;
            if (el.getAttribute("aria-disabled") === "true") return false;
            for (let node = el; node instanceof Element; node = node.parentElement) {
              if (node.hasAttribute("hidden")) return false;
              if (node.hasAttribute("inert")) return false;
              if (node.getAttribute("aria-hidden") === "true") return false;
              const style = window.getComputedStyle(node);
              if (style.display === "none") return false;
              if (style.visibility === "hidden" || style.visibility === "collapse") return false;
              if (style.opacity === "0") return false;
            }
            const rect = el.getBoundingClientRect();
            if (rect.width <= 0 || rect.height <= 0) return false;
            return true;
          };
          const firstVisible = (selector) => {
            for (const el of document.querySelectorAll(selector)) {
              if (isVisible(el)) return el;
            }
            return null;
          };
          const firstVisibleWithin = (root, selector) => {
            for (const el of root.querySelectorAll(selector)) {
              if (isVisible(el)) return el;
            }
            return null;
          };
          const captchaSelectors = [
            'iframe[src*="recaptcha"]',
            'iframe[src*="hcaptcha"]',
            'iframe[src*="challenges.cloudflare.com"]',
            ".g-recaptcha",
            ".h-captcha",
            "[data-sitekey]",
            ".cf-turnstile"
          ];
          for (const selector of captchaSelectors) {
            if (firstVisible(selector)) return "verification";
          }
          const otp = firstVisible(
            'input[autocomplete="one-time-code"], input[name*="otp" i], input[id*="otp" i], input[name*="mfa" i]');
          if (otp) return "verification";
          const password = firstVisible('input[type="password"]');
          if (!password) return "none";
          const form = password.closest("form") || document.body;
          const email = firstVisibleWithin(
            form,
            'input[type="email"], input[autocomplete="username"], input[autocomplete="email"]');
          const registration = password.getAttribute("autocomplete") === "new-password"
            || !!firstVisibleWithin(form, 'input[autocomplete="new-password"]');
          const passwordChange = /reset|change|new password|confirm password|create password/i.test(form.innerText || "")
            || /reset|change-password|register|signup/i.test(location.pathname) || form.querySelectorAll('input[type="password"]').length > 1;
          if (registration || passwordChange) return "registration";
          return "none";
        }
        """;

    private static async Task<BrowserInterventionKind> ClassifyInterventionAsync(
        IPage page,
        CancellationToken cancellationToken)
    {
        try
        {
            var kind = await page.EvaluateAsync<string>(ClassifyInterventionScript)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return kind switch
            {
                "registration" => BrowserInterventionKind.AccountRegistrationRequired,
                "authentication" => BrowserInterventionKind.AuthenticationRequired,
                "verification" => BrowserInterventionKind.HumanVerificationRequired,
                _ => BrowserInterventionKind.None
            };
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            return BrowserInterventionKind.AuthenticationRequired;
        }
    }

    private async Task RestoreAllowedPageAsync(SessionBrowser session, CancellationToken cancellationToken)
    {
        if (session.LastAllowedUrl is not null)
        {
            try
            {
                session.DeniedNavigation = false;
                await session.Page.GotoAsync(
                        session.LastAllowedUrl,
                        new PageGotoOptions
                        {
                            Timeout = TimeoutMs(),
                            WaitUntil = WaitUntilState.DOMContentLoaded
                        })
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (IsAllowed(session, session.Page.Url))
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is PlaywrightException or OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
            }
        }

        await CloseQuietlyAsync(session.Page).ConfigureAwait(false);
        session.AcceptingMainPage = true;
        try
        {
            session.Page = await session.Context.NewPageAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            session.AcceptingMainPage = false;
        }

        session.Page.SetDefaultTimeout(TimeoutMs());
        session.Page.SetDefaultNavigationTimeout(TimeoutMs());
    }

    private static int BeginCall(SessionBrowser session)
    {
        session.CallPages = session.Context.Pages.ToHashSet();
        var call = session.OperationCall + 1;
        session.OperationCall = call;
        return call;
    }

    private void CancelCall(SessionBrowser session, int call)
    {
        Volatile.Write(ref session.CancelledCall, call);
        var route = session.InFlightRoute;
        if (route is not null)
        {
            _ = AbortQuietlyAsync(route);
        }

        _ = StopPageLoadAsync(session, session.Page);
    }

    private async Task FinishCancellationAsync(SessionBrowser session)
    {
        Volatile.Write(ref session.CancelledCall, session.OperationCall);
        var route = session.InFlightRoute;
        if (route is not null)
        {
            await AbortQuietlyAsync(route).ConfigureAwait(false);
        }

        await StopPageLoadAsync(session, session.Page).ConfigureAwait(false);
    }

    private async Task StopPageLoadAsync(SessionBrowser session, IPage page)
    {
        try
        {
            var client = await session.Context.NewCDPSessionAsync(page).ConfigureAwait(false);
            try
            {
                await client.SendAsync("Page.stopLoading").ConfigureAwait(false);
            }
            finally
            {
                await client.DetachAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is PlaywrightException or ObjectDisposedException)
        {
        }
    }

    private static async Task<bool> IsAttachedAsync(ILocator handle)
    {
        try
        {
            return await handle.EvaluateAsync<bool>("el => !!el.isConnected").ConfigureAwait(false);
        }
        catch (PlaywrightException)
        {
            return false;
        }
    }

    private float TimeoutMs() => (float)OperationTimeout.TotalMilliseconds;

    private static string[] Retarget(IReadOnlyList<string> configured, string? liveOrigin, int? livePort)
    {
        var results = new List<string>();
        foreach (var entry in configured)
        {
            if (!Uri.TryCreate(entry, UriKind.Absolute, out var uri))
            {
                continue;
            }

            if (liveOrigin is not null
                && livePort is not null
                && BrowserTargetPolicy.IsLoopback(entry)
                && uri.Port != livePort.Value)
            {
                AddOrigin(results, liveOrigin);
                continue;
            }

            AddOrigin(results, entry);
        }

        return results.ToArray();
    }

    private static void AddOrigin(List<string> results, string origin)
    {
        if (!results.Contains(origin, StringComparer.Ordinal))
        {
            results.Add(origin);
        }
    }

    private enum BrowserSnapshotSettle
    {
        None,
        Automatic,
        Stable
    }

    private async Task<BrowserResult> CaptureWithRetryAsync(
        SessionBrowser session,
        Guid sessionId,
        string operation,
        BrowserSnapshotSettle settle,
        int? timeoutMs,
        CancellationToken cancellationToken)
    {
        bool? settled = null;
        try
        {
            if (settle != BrowserSnapshotSettle.None)
            {
                var budget = settle == BrowserSnapshotSettle.Automatic
                    ? BrowserPageSettle.AutomaticDeadlineMs
                    : Math.Clamp(
                        timeoutMs ?? BrowserToolLimits.DefaultObserveTimeoutMs,
                        BrowserToolLimits.MinObserveTimeoutMs,
                        BrowserToolLimits.MaxObserveTimeoutMs);
                var started = Environment.TickCount64;
                var reached = await BrowserPageSettle.WaitAsync(session.Page, budget, cancellationToken).ConfigureAwait(false);
                settled = reached;
                _logger.LogInformation(
                    "browser.settle operation={Operation} waitFor={WaitFor} settled={Settled} durationMs={DurationMs}",
                    operation,
                    settle == BrowserSnapshotSettle.Stable ? "stable" : "automatic",
                    reached,
                    Environment.TickCount64 - started);
            }

            return await AttachDownloadsAsync(
                session,
                new BrowserResult(null, await CaptureMarkedAsync(session, sessionId, settled, cancellationToken).ConfigureAwait(false)),
                cancellationToken).ConfigureAwait(false);
        }
        catch (PlaywrightException ex) when (BrowserFailureClassifier.IsTransientCapture(ex.Message))
        {
            LogBrowserFailure(operation, "capture", BrowserFailureClassifier.Classify(ex.Message).Reason);
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
                return await AttachDownloadsAsync(
                    session,
                    new BrowserResult(
                        null,
                        await CaptureMarkedAsync(session, sessionId, settled, cancellationToken).ConfigureAwait(false)),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (PlaywrightException retry)
            {
                return await AttachDownloadsAsync(
                    session,
                    await FailAsync(session, operation, "capture", retry).ConfigureAwait(false),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (PlaywrightException ex)
        {
            return await AttachDownloadsAsync(
                session,
                await FailAsync(session, operation, "capture", ex).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<BrowserResult> FailAsync(
        SessionBrowser? session,
        string operation,
        string stage,
        PlaywrightException exception)
    {
        var decision = BrowserFailureClassifier.Classify(exception.Message);
        var reportedStage = decision.Reason is "pageClosed" or "contextClosed" ? "lifecycle" : stage;
        LogBrowserFailure(operation, reportedStage, decision.Reason);
        if (session is not null
            && decision.Reason is "pageClosed" or "contextClosed"
            && PageIsClosed(session))
        {
            await CloseQuietlyAsync(session.Context).ConfigureAwait(false);
            ForgetClosed(session);
        }

        return decision.Code == "provider_unavailable" ? Unavailable() : Result(decision.Code);
    }

    private static bool PageIsClosed(SessionBrowser session)
    {
        try
        {
            return session.Page.IsClosed;
        }
        catch (PlaywrightException)
        {
            return true;
        }
    }

    private void LogBrowserFailure(string operation, string stage, string reason) =>
        _logger.LogWarning(
            "browser.operation.failure operation={Operation} stage={Stage} reason={Reason}",
            operation,
            stage,
            reason);

    private static BrowserResult Unavailable() => Result("provider_unavailable");

    private static BrowserResult Result(string code, IReadOnlyList<string>? allowedActions = null) =>
        new(code, null, allowedActions);

    private static bool IsTimeout(Exception exception) =>
        exception is TimeoutException
        || (exception is PlaywrightException && exception.Message.Contains("Timeout", StringComparison.Ordinal));

    private bool IsCancelled(SessionBrowser session, int call) =>
        Volatile.Read(ref session.CancelledCall) == call;

    private static bool IsCancel(Exception exception, CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested
        && exception is PlaywrightException or TimeoutException;

    private static async Task CloseQuietlyAsync(IPage page)
    {
        try
        {
            await page.CloseAsync().ConfigureAwait(false);
        }
        catch (PlaywrightException)
        {
        }
    }

    private static async Task CloseQuietlyAsync(IBrowserContext context)
    {
        try
        {
            await context.CloseAsync().ConfigureAwait(false);
        }
        catch (PlaywrightException)
        {
        }
    }

    private static void DisposeQuietly(IPlaywright? playwright)
    {
        if (playwright is null)
        {
            return;
        }

        try
        {
            playwright.Dispose();
        }
        catch (Exception ex) when (ex is PlaywrightException or ObjectDisposedException)
        {
        }
    }

    private sealed class BrowserLaunchException : Exception;

    private sealed record OwnedFrame(IFrame Frame, int Generation);

    private static void AdvanceGeneration(SessionBrowser session)
    {
        Interlocked.Increment(ref session.Generation);
        session.Frames.Clear();
    }

    private sealed class SessionBrowser(IBrowserContext context, IPage page)
    {
        public BrowserEnvironment Environment { get; init; } = new(null, "en-US", TimeZoneInfo.Local.Id, false, false, 1);
        public ConcurrentDictionary<string, OwnedFrame> Frames { get; } = new();
        public Dictionary<IPage, PageEmulateMediaOptions> Media { get; } = new();
        public string? GeolocationOrigin { get; set; }
        public bool Offline { get; set; }
        public IDialog? Dialog { get; set; }
        public TaskCompletionSource DialogSignal { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? PendingAction { get; set; }
        public List<BrowserRouteRule> Rules { get; } = [];
        public List<string> Console { get; } = [];
        public Dictionary<string, IRequest> Network { get; } = new();
        public ProtectedBrowserValues ProtectedValues { get; } = new();

        public IBrowserContext Context { get; } = context;

        public IPage Page { get; set; } = page;

        public bool Persistent { get; init; }

        public FileStream? ProfileLease { get; init; }

        public IPlaywright? PlaywrightDriver { get; init; }

        public Guid? AgentInstanceId { get; init; }

        public int RuntimeClosed;

        public int Generation;

        public string? LastAllowedUrl { get; set; }

        public bool DeniedNavigation { get; set; }

        public string? PopupCode { get; set; }

        public IPage? PendingOpenedPage { get; set; }

        public bool TimedOut { get; set; }

        public bool AcceptingMainPage { get; set; }

        public int OperationCall { get; set; }

        public int CancelledCall = -1;

        public IRoute? InFlightRoute { get; set; }

        public object PopupGate { get; } = new();

        public List<Task> PopupCloses { get; } = [];
        public HashSet<IPage> CallPages { get; set; } = [];

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public List<PageBinding> Pages { get; } = [];

        public List<BrowserDownload> StagedDownloads { get; } = [];

        public List<IDownload> PendingDownloads { get; } = [];
    }

    private sealed record BrowserCloseResult(string Status);


}
