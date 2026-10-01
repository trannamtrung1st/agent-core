using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;

namespace AgentCore.Infrastructure.Browser;

public sealed class PlaywrightBrowserSession : IBrowserSession, IBrowserSessionLease, IHostedService
{
    private const string DescribeElement = """
        el => {
          const tag = (el.tagName || "").toLowerCase();
          let role = el.getAttribute("role");
          if (!role) {
            if (tag === "a") role = "link";
            else if (tag === "button") role = "button";
            else if (tag === "select") role = "combobox";
            else if (tag === "textarea" || tag === "input") role = "textbox";
            else role = tag || "generic";
          }
          let name = "";
          if (el.labels && el.labels.length > 0) name = (el.labels[0].innerText || "").trim();
          if (!name) name = (el.getAttribute("aria-label") || "").trim();
          if (!name && tag !== "input") name = (el.innerText || "").trim();
          return JSON.stringify({ role, name: name.slice(0, 200) });
        }
        """;

    private const string ReadSecrets = """
        () => {
          const values = [];
          try {
            for (let i = 0; i < localStorage.length; i++) values.push(localStorage.getItem(localStorage.key(i)) || "");
          } catch { }
          try {
            for (let i = 0; i < sessionStorage.length; i++) values.push(sessionStorage.getItem(sessionStorage.key(i)) || "");
          } catch { }
          document.querySelectorAll('input[type="password"]').forEach(el => values.push(el.value || ""));
          return values;
        }
        """;

    private readonly BrowserOptions _options;
    private readonly ILogger _logger;
    private readonly LoopbackBrowserFixtureHost _fixture;
    private readonly SemaphoreSlim _launch = new(1, 1);
    private readonly ConcurrentDictionary<Guid, SessionBrowser> _sessions = new();
    private readonly ConcurrentDictionary<string, LiveElement> _refs = new();
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private BrowserHostPolicy _policy;
    private int _stopped;

    public PlaywrightBrowserSession(BrowserOptions options, ILoggerFactory? loggerFactory)
    {
        _options = options;
        _logger = loggerFactory?.CreateLogger<PlaywrightBrowserSession>()
            ?? NullLogger<PlaywrightBrowserSession>.Instance;
        _fixture = new LoopbackBrowserFixtureHost(_logger);
        _policy = options.ToHostPolicy();
    }

    internal LoopbackBrowserFixtureHost Fixture => _fixture;

    internal bool? LaunchedHeadless { get; private set; }

    internal TimeSpan OperationTimeout { get; set; } = TimeSpan.FromSeconds(25);

    public bool IsAvailable => _options.Enabled && _fixture.IsAvailable && Volatile.Read(ref _stopped) == 0;

    public BrowserHostPolicy HostPolicy => _policy;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _policy = _options.ToHostPolicy();
            return;
        }

        await _fixture.StartAsync(_options.FixturePort, cancellationToken).ConfigureAwait(false);
        if (!_fixture.IsAvailable || _fixture.Origin is null || _fixture.Port is null)
        {
            _policy = _options.ToHostPolicy() with { TargetOrigins = [] };
            return;
        }

        var origins = _options.FixturePort == 0
            ? new[] { _fixture.Origin }
            : MatchingOrigins(_options, _fixture.Port.Value);
        _policy = _options.ToHostPolicy() with { TargetOrigins = origins };
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 1)
        {
            return;
        }

        foreach (var session in _sessions.Values)
        {
            await CloseQuietlyAsync(session.Context).ConfigureAwait(false);
        }

        _sessions.Clear();
        _refs.Clear();
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
        await _fixture.DisposeAsync().ConfigureAwait(false);
    }

    public ValueTask<Uri?> GetCurrentUrlAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_sessions.TryGetValue(sessionId, out var session) || !IsAllowed(session.Page.Url))
        {
            return new ValueTask<Uri?>(result: null);
        }

        return new ValueTask<Uri?>(Uri.TryCreate(session.Page.Url, UriKind.Absolute, out var uri) ? uri : null);
    }

    public async ValueTask<BrowserOperationResult> NavigateAsync(
        BrowserNavigateRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return Unavailable();
        }

        var decision = BrowserTargetPolicy.EvaluateDestination(request.Url.AbsoluteUri, _policy.TargetOrigins);
        if (!decision.Allowed)
        {
            return Result(decision.Code ?? "target_denied");
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
                await session.Page.GotoAsync(
                        request.Url.AbsoluteUri,
                        new PageGotoOptions
                        {
                            Timeout = TimeoutMs(),
                            WaitUntil = WaitUntilState.DOMContentLoaded
                        })
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (PlaywrightException) when (session.DeniedNavigation || session.PopupCode is not null || session.TimedOut)
            {
            }

            if (session.TimedOut)
            {
                return Result("timeout");
            }

            if (session.PopupCode is not null)
            {
                return Result(session.PopupCode);
            }

            if (session.DeniedNavigation || !IsAllowed(session.Page.Url))
            {
                await RestoreAllowedPageAsync(session, cancellationToken).ConfigureAwait(false);
                return Result("target_denied");
            }

            session.Generation++;
            session.LastAllowedUrl = session.Page.Url;
            return new BrowserOperationResult(null, await CaptureAsync(session, request.SessionId, cancellationToken).ConfigureAwait(false));
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
            return Result("timeout");
        }
        catch (Exception ex) when (ex is PlaywrightException or BrowserLaunchException)
        {
            _logger.LogWarning("Browser provider is unavailable.");
            return Unavailable();
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

    public async ValueTask<BrowserOperationResult> ObserveAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable || !_sessions.TryGetValue(sessionId, out var session))
        {
            return Unavailable();
        }

        var entered = false;
        try
        {
            await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            if (!IsAllowed(session.Page.Url))
            {
                return Unavailable();
            }

            return new BrowserOperationResult(null, await CaptureAsync(session, sessionId, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsCancel(ex, cancellationToken))
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception ex) when (IsTimeout(ex))
        {
            return Result("timeout");
        }
        catch (PlaywrightException)
        {
            _logger.LogWarning("Browser provider is unavailable.");
            return Unavailable();
        }
        finally
        {
            if (entered)
            {
                session.Gate.Release();
            }
        }
    }

    public async ValueTask<BrowserOperationResult> ActAsync(
        BrowserActRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return Unavailable();
        }

        if (!_refs.TryGetValue(request.Ref, out var live))
        {
            return Result("stale_reference");
        }

        if (live.SessionId != request.SessionId)
        {
            return Result("forbidden");
        }

        if (!_sessions.TryGetValue(request.SessionId, out var session) || live.Generation != session.Generation)
        {
            return Result("stale_reference");
        }

        var current = await GetCurrentUrlAsync(request.SessionId, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return Unavailable();
        }

        var decision = BrowserTargetPolicy.EvaluateAct(
            _policy.InteractionMode,
            current.AbsoluteUri,
            _policy.TargetOrigins);
        if (!decision.Allowed)
        {
            return Result(decision.Code ?? "forbidden");
        }

        var entered = false;
        try
        {
            await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            if (live.Generation != session.Generation)
            {
                return Result("stale_reference");
            }

            if (request.Operation is "fill" or "select" or "press" && string.IsNullOrEmpty(request.Value))
            {
                return Result("invalid");
            }

            if (!await IsAttachedAsync(live.Handle).ConfigureAwait(false))
            {
                return Result("stale_reference");
            }

            var before = session.Page.Url;
            session.DeniedNavigation = false;
            session.PopupCode = null;
            session.TimedOut = false;
            var call = BeginCall(session);
            using var registration = cancellationToken.Register(() => CancelCall(session, call));
            try
            {
                await PerformActAsync(live.Handle, request, cancellationToken).ConfigureAwait(false);
            }
            catch (PlaywrightException) when (session.PopupCode is not null || session.DeniedNavigation || session.TimedOut)
            {
            }

            if (session.TimedOut)
            {
                return Result("timeout");
            }

            if (session.PopupCode is not null)
            {
                return Result(session.PopupCode);
            }

            if (session.DeniedNavigation || !IsAllowed(session.Page.Url))
            {
                await RestoreAllowedPageAsync(session, cancellationToken).ConfigureAwait(false);
                return Result("target_denied");
            }

            if (!string.Equals(before, session.Page.Url, StringComparison.Ordinal))
            {
                session.Generation++;
                session.LastAllowedUrl = session.Page.Url;
            }

            return new BrowserOperationResult(null, await CaptureAsync(session, request.SessionId, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            await FinishCancellationAsync(session).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (IsCancel(ex, cancellationToken))
        {
            await FinishCancellationAsync(session).ConfigureAwait(false);
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception ex) when (IsTimeout(ex))
        {
            return Result("timeout");
        }
        catch (PlaywrightException)
        {
            _logger.LogWarning("Browser provider is unavailable.");
            return Unavailable();
        }
        finally
        {
            await SettlePopupsAsync(session).ConfigureAwait(false);
            if (entered)
            {
                session.Gate.Release();
            }
        }
    }

    internal IBrowserContext? ContextFor(Guid sessionId) =>
        _sessions.TryGetValue(sessionId, out var session) ? session.Context : null;

    public async ValueTask ReleaseAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_sessions.TryRemove(sessionId, out var session))
        {
            RemoveRefs(sessionId);
            await CloseQuietlyAsync(session.Context).ConfigureAwait(false);
        }
    }

    private async Task<SessionBrowser> EnsureSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (_sessions.TryGetValue(sessionId, out var existing))
        {
            return existing;
        }

        var browser = await EnsureBrowserAsync(cancellationToken).ConfigureAwait(false);
        var context = await browser.NewContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        var page = await context.NewPageAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        page.SetDefaultTimeout(TimeoutMs());
        page.SetDefaultNavigationTimeout(TimeoutMs());
        var session = new SessionBrowser(context, page);
        context.Page += (_, opened) => OnContextPage(session, opened);
        await context.RouteAsync("**/*", route => RouteAsync(session, route)).ConfigureAwait(false);
        if (!_sessions.TryAdd(sessionId, session))
        {
            await CloseQuietlyAsync(context).ConfigureAwait(false);
            return _sessions[sessionId];
        }

        return session;
    }

    private async Task<IBrowser> EnsureBrowserAsync(CancellationToken cancellationToken)
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
                var options = new BrowserTypeLaunchOptions { Headless = _options.Headless };
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

    private async Task RouteAsync(SessionBrowser session, IRoute route)
    {
        var url = route.Request.Url;
        IPage? page = null;
        try
        {
            page = route.Request.Frame?.Page;
        }
        catch (Exception)
        {
            page = null;
        }

        try
        {
            if (!ReferenceEquals(page, session.Page))
            {
                if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    var popup = BrowserTargetPolicy.EvaluatePopup(url, _policy.TargetOrigins);
                    session.PopupCode ??= popup.Code ?? "unsupported_operation";
                }

                await route.AbortAsync().ConfigureAwait(false);
                if (page is not null)
                {
                    await CloseQuietlyAsync(page).ConfigureAwait(false);
                }

                return;
            }

            if (url.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
            {
                await route.ContinueAsync().ConfigureAwait(false);
                return;
            }

            if (IsCancelled(session, session.OperationCall))
            {
                await route.AbortAsync().ConfigureAwait(false);
                return;
            }

            if (!IsAllowed(url))
            {
                session.DeniedNavigation = true;
                await route.AbortAsync().ConfigureAwait(false);
                return;
            }

            await FulfillWithoutLeavingPolicyAsync(session, route, url).ConfigureAwait(false);
        }
        catch (PlaywrightException)
        {
            await AbortQuietlyAsync(route).ConfigureAwait(false);
        }
    }

    private async Task FulfillWithoutLeavingPolicyAsync(SessionBrowser session, IRoute route, string url)
    {
        var call = session.OperationCall;
        session.InFlightRoute = route;
        IAPIResponse response;
        try
        {
            if (IsCancelled(session, call))
            {
                await AbortQuietlyAsync(route).ConfigureAwait(false);
                return;
            }

            response = await route.FetchAsync(new RouteFetchOptions
            {
                MaxRedirects = 0,
                Timeout = TimeoutMs()
            }).ConfigureAwait(false);
            if (IsCancelled(session, call))
            {
                await AbortQuietlyAsync(route).ConfigureAwait(false);
                return;
            }
        }
        catch (PlaywrightException ex) when (IsTimeout(ex))
        {
            session.TimedOut = true;
            await AbortQuietlyAsync(route).ConfigureAwait(false);
            return;
        }
        catch (PlaywrightException) when (IsCancelled(session, call))
        {
            await AbortQuietlyAsync(route).ConfigureAwait(false);
            return;
        }
        finally
        {
            if (ReferenceEquals(session.InFlightRoute, route))
            {
                session.InFlightRoute = null;
            }
        }

        if (IsRedirect(response.Status) && !RedirectStaysAllowed(url, response.Headers))
        {
            session.DeniedNavigation = true;
            await AbortQuietlyAsync(route).ConfigureAwait(false);
            return;
        }

        if (IsCancelled(session, call))
        {
            await AbortQuietlyAsync(route).ConfigureAwait(false);
            return;
        }

        await route.FulfillAsync(new RouteFulfillOptions { Response = response }).ConfigureAwait(false);
    }

    private bool RedirectStaysAllowed(string requestUrl, IDictionary<string, string> headers)
    {
        string? location = null;
        foreach (var header in headers)
        {
            if (header.Key.Equals("location", StringComparison.OrdinalIgnoreCase))
            {
                location = header.Value;
                break;
            }
        }

        return !string.IsNullOrWhiteSpace(location)
            && Uri.TryCreate(requestUrl, UriKind.Absolute, out var baseUri)
            && Uri.TryCreate(baseUri, location, out var resolved)
            && IsAllowed(resolved.AbsoluteUri);
    }

    private static bool IsRedirect(int status) => status is 301 or 302 or 303 or 307 or 308;

    private static async Task AbortQuietlyAsync(IRoute route)
    {
        try
        {
            await route.AbortAsync().ConfigureAwait(false);
        }
        catch (PlaywrightException)
        {
        }
    }

    private async Task PerformActAsync(IElementHandle handle, BrowserActRequest request, CancellationToken cancellationToken)
    {
        var timeout = TimeoutMs();
        var operation = request.Operation;
        if (operation == "click")
        {
            await handle.ClickAsync(new ElementHandleClickOptions { Timeout = timeout })
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrEmpty(request.Value))
        {
            return;
        }

        if (operation == "fill")
        {
            await handle.FillAsync(request.Value, new ElementHandleFillOptions { Timeout = timeout })
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (operation == "select")
        {
            await handle.SelectOptionAsync(request.Value, new ElementHandleSelectOptionOptions { Timeout = timeout })
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (operation == "press")
        {
            await handle.PressAsync(request.Value, new ElementHandlePressOptions { Timeout = timeout })
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<BrowserObservation> CaptureAsync(
        SessionBrowser session,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        RemoveRefs(sessionId);
        var title = await session.Page.TitleAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        var text = await session.Page.Locator("body").InnerTextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        var secrets = await CollectSecretsAsync(session, cancellationToken).ConfigureAwait(false);
        title = Redact(title, secrets);
        text = Redact(text, secrets);
        var truncated = text.Length > BrowserToolLimits.MaxVisibleTextLength;
        var elements = await CollectElementsAsync(session, sessionId, secrets, cancellationToken).ConfigureAwait(false);
        return new BrowserObservation(
            session.Page.Url,
            Clip(title, BrowserToolLimits.MaxTitleLength),
            Clip(text, BrowserToolLimits.MaxVisibleTextLength),
            truncated,
            elements);
    }

    private async Task<IReadOnlyList<BrowserElement>> CollectElementsAsync(
        SessionBrowser session,
        Guid sessionId,
        IReadOnlyList<string> secrets,
        CancellationToken cancellationToken)
    {
        var locator = session.Page.Locator("a, button, input, select, textarea");
        var count = Math.Min(await locator.CountAsync().WaitAsync(cancellationToken).ConfigureAwait(false), BrowserToolLimits.MaxElements);
        var elements = new List<BrowserElement>(count);
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var handle = await locator.Nth(index).ElementHandleAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            if (handle is null)
            {
                continue;
            }

            var described = await handle.EvaluateAsync<string>(DescribeElement).WaitAsync(cancellationToken).ConfigureAwait(false);
            var role = "generic";
            var name = string.Empty;
            if (!string.IsNullOrWhiteSpace(described))
            {
                using var document = JsonDocument.Parse(described);
                if (document.RootElement.TryGetProperty("role", out var roleProperty))
                {
                    role = roleProperty.GetString() ?? role;
                }

                if (document.RootElement.TryGetProperty("name", out var nameProperty))
                {
                    name = nameProperty.GetString() ?? string.Empty;
                }
            }

            var token = MintToken();
            _refs[token] = new LiveElement(sessionId, session.Generation, handle);
            elements.Add(new BrowserElement(
                token,
                Clip(Redact(role, secrets), BrowserToolLimits.MaxRoleLength),
                Clip(Redact(name, secrets), BrowserToolLimits.MaxAccessibleNameLength)));
        }

        return elements;
    }

    private async Task<IReadOnlyList<string>> CollectSecretsAsync(SessionBrowser session, CancellationToken cancellationToken)
    {
        var secrets = new List<string>();
        var cookies = await session.Context.CookiesAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        foreach (var cookie in cookies)
        {
            AddSecret(secrets, cookie.Value);
        }

        var stored = await session.Page.EvaluateAsync<string[]>(ReadSecrets).WaitAsync(cancellationToken).ConfigureAwait(false);
        foreach (var value in stored)
        {
            AddSecret(secrets, value);
        }

        secrets.Sort(static (left, right) => right.Length.CompareTo(left.Length));
        return secrets;
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
                if (IsAllowed(session.Page.Url))
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

        _ = StopPageLoadAsync(session);
    }

    private async Task FinishCancellationAsync(SessionBrowser session)
    {
        Volatile.Write(ref session.CancelledCall, session.OperationCall);
        var route = session.InFlightRoute;
        if (route is not null)
        {
            await AbortQuietlyAsync(route).ConfigureAwait(false);
        }

        await StopPageLoadAsync(session).ConfigureAwait(false);
    }

    private async Task StopPageLoadAsync(SessionBrowser session)
    {
        try
        {
            var client = await session.Context.NewCDPSessionAsync(session.Page).ConfigureAwait(false);
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

    private void OnContextPage(SessionBrowser session, IPage opened)
    {
        if (session.AcceptingMainPage || ReferenceEquals(opened, session.Page))
        {
            return;
        }

        var close = CloseQuietlyAsync(opened);
        lock (session.PopupGate)
        {
            session.PopupCloses.Add(close);
        }
    }

    private async Task SettlePopupsAsync(SessionBrowser session)
    {
        try
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                await ClosePopupsAsync(session).ConfigureAwait(false);
                Task[] pending;
                lock (session.PopupGate)
                {
                    pending = session.PopupCloses.ToArray();
                    session.PopupCloses.Clear();
                }

                if (pending.Length > 0)
                {
                    await Task.WhenAll(pending).ConfigureAwait(false);
                }

                var extras = session.Context.Pages.Count(page => !ReferenceEquals(page, session.Page));
                var stillClosing = false;
                lock (session.PopupGate)
                {
                    stillClosing = session.PopupCloses.Count > 0;
                }

                if (extras == 0 && !stillClosing)
                {
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is PlaywrightException or ObjectDisposedException)
        {
        }
    }

    private static async Task ClosePopupsAsync(SessionBrowser session)
    {
        foreach (var page in session.Context.Pages.ToArray())
        {
            if (ReferenceEquals(page, session.Page))
            {
                continue;
            }

            await CloseQuietlyAsync(page).ConfigureAwait(false);
        }
    }

    private static async Task<bool> IsAttachedAsync(IElementHandle handle)
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

    private bool IsAllowed(string url) =>
        BrowserTargetPolicy.EvaluateDestination(url, _policy.TargetOrigins).Allowed;

    private void RemoveRefs(Guid sessionId)
    {
        foreach (var pair in _refs)
        {
            if (pair.Value.SessionId == sessionId)
            {
                _refs.TryRemove(pair.Key, out _);
            }
        }
    }

    private float TimeoutMs() => (float)OperationTimeout.TotalMilliseconds;

    private static IReadOnlyList<string> MatchingOrigins(BrowserOptions options, int port)
    {
        var matched = new List<string>();
        foreach (var entry in options.TargetOrigins ?? [])
        {
            if (Uri.TryCreate(entry, UriKind.Absolute, out var uri)
                && uri.Port == port
                && BrowserTargetPolicy.IsLoopback(entry))
            {
                matched.Add(entry);
            }
        }

        return matched;
    }

    private static void AddSecret(List<string> secrets, string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length < 4 || secrets.Contains(value, StringComparer.Ordinal))
        {
            return;
        }

        secrets.Add(value);
    }

    private static string Redact(string? text, IReadOnlyList<string> secrets)
    {
        var current = text ?? string.Empty;
        foreach (var secret in secrets)
        {
            current = current.Replace(secret, "[redacted]", StringComparison.Ordinal);
        }

        return current;
    }

    private static string Clip(string text, int max) =>
        text.Length <= max ? text : text[..max];

    private static string MintToken()
    {
        var encoded = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return "el_" + encoded;
    }

    private static BrowserOperationResult Unavailable() => Result("provider_unavailable");

    private static BrowserOperationResult Result(string code) => new(code, null);

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

    private sealed class BrowserLaunchException : Exception;

    private sealed class SessionBrowser(IBrowserContext context, IPage page)
    {
        public IBrowserContext Context { get; } = context;

        public IPage Page { get; set; } = page;

        public int Generation { get; set; }

        public string? LastAllowedUrl { get; set; }

        public bool DeniedNavigation { get; set; }

        public string? PopupCode { get; set; }

        public bool TimedOut { get; set; }

        public bool AcceptingMainPage { get; set; }

        public int OperationCall { get; set; }

        public int CancelledCall = -1;

        public IRoute? InFlightRoute { get; set; }

        public object PopupGate { get; } = new();

        public List<Task> PopupCloses { get; } = [];

        public SemaphoreSlim Gate { get; } = new(1, 1);
    }

    private sealed record LiveElement(Guid SessionId, int Generation, IElementHandle Handle);
}
