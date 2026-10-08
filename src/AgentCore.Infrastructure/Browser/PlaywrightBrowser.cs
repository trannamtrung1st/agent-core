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

public sealed partial class PlaywrightBrowser : AgentCore.Application.Ports.IBrowser, IBrowserPasswordSink, IBrowserLease, IBrowserProfileBinding, IBrowserContextUse, IBrowserRuntimeReadiness, IHostedService
{
    private const string DescribeElement = """
        el => {
          const tag = (el.tagName || "").toLowerCase();
          const type = (el.getAttribute("type") || "").toLowerCase();
          let role = el.getAttribute("role");
          if (!role) {
            if (tag === "a") role = "link";
            else if (tag === "button" || type === "button" || type === "submit") role = "button";
            else if (tag === "select") role = "combobox";
            else if (type === "checkbox") role = "checkbox";
            else if (type === "radio") role = "radio";
            else if (type === "file") role = "button";
            else if (tag === "textarea" || tag === "input") role = "textbox";
            else role = tag || "generic";
          }
          let name = "";
          const labelText = (label) => ((label && (label.innerText || label.textContent)) || "").trim();
          if (el.labels && el.labels.length > 0) {
            name = Array.from(el.labels).map(labelText).filter(Boolean).join(" ");
          }
          if (!name && el.id) {
            const explicit = document.querySelector(`label[for="${CSS.escape(el.id)}"]`);
            name = labelText(explicit);
          }
          if (!name) name = (el.getAttribute("aria-label") || "").trim();
          if (!name && tag !== "input") name = (el.innerText || el.textContent || "").trim();
          const humanize = (value) => (value || "")
            .replace(/[-_]+/g, " ")
            .replace(/([a-z])([A-Z])/g, "$1 $2")
            .replace(/\s+/g, " ")
            .trim();
          if (!name && el.htmlFor) name = humanize(el.htmlFor);
          if (!name && el.id) name = humanize(el.id);
          if (tag === "input" && type === "hidden") return "null";
          if (type === "file") {
            let node = el.parentElement;
            for (let depth = 0; depth < 5 && node && node !== document.body && node !== document.documentElement; depth += 1, node = node.parentElement) {
              if (node.children.length > 4) continue;
              for (const child of node.children) {
                if (child.contains(el)) continue;
                const caption = ((child.innerText || child.textContent) || "").replace(/\s+/g, " ").trim();
                if (caption && caption.length <= 40 && !/upload|browse/i.test(caption)) {
                  name = caption;
                  depth = 5;
                  break;
                }
              }
            }
          }
          let actions = ["click"];
          if (type === "file") actions = ["upload"];
          else if (tag === "select") actions = ["select"];
          else if (role === "checkbox" || role === "switch" || type === "checkbox") actions = ["check", "uncheck"];
          else if (role === "textbox" || role === "searchbox" || tag === "textarea" || (tag === "input" && type !== "button" && type !== "submit" && type !== "checkbox" && type !== "radio" && type !== "file")) actions = ["fill", "press"];
          else actions = ["button", "link", "radio", "option", "tab", "menuitem", "treeitem", "combobox"].includes(role) || el.hasAttribute("tabindex") || el.hasAttribute("onclick") ? ["click"] : [];
          const source = (() => {
            if (tag !== "label") return el;
            const linkedId = el.htmlFor || el.getAttribute("for");
            const linked = linkedId ? document.getElementById(linkedId) : null;
            return linked || el.querySelector("input, select, textarea") || el;
          })();
          const sourceTag = (source.tagName || "").toLowerCase();
          const sourceType = (source.getAttribute("type") || "").toLowerCase();
          const sourceRole = (source.getAttribute("role") || role || "").toLowerCase();
          const haystack = [name, source.id, source.getAttribute("name"), source.getAttribute("autocomplete"), source.getAttribute("aria-label")]
            .filter(Boolean).join(" ").toLowerCase();
          const sensitiveTerms = ["password", "passwd", "passcode", "secret", "token", "api key", "apikey", "access key", "private key", "client secret", "authorization", "one-time-code", "otp"];
          const sensitive = sourceType === "password"
            || sourceType === "hidden"
            || (source.getAttribute("autocomplete") || "").toLowerCase().includes("one-time-code")
            || sensitiveTerms.some(term => haystack.includes(term));
          if (sourceType === "password") actions = ["fill_credential"];
          else if (sensitive) return "null";
          const state = {};
          if (!sensitive && sourceType !== "file") {
            if (sourceTag === "select") {
              const option = source.selectedOptions && source.selectedOptions[0];
              if (option) state.selectedText = ((option.label || option.textContent) || "").trim().slice(0, 500);
            } else if (sourceType === "checkbox" || sourceType === "radio") {
              state.checked = source.checked === true;
            } else if (sourceRole === "checkbox" || sourceRole === "radio" || sourceRole === "switch") {
              const ariaChecked = source.getAttribute("aria-checked");
              if (ariaChecked === "true" || ariaChecked === "false") state.checked = ariaChecked === "true";
              else if (typeof source.checked === "boolean") state.checked = source.checked === true;
            } else if (sourceTag === "textarea" || (sourceTag === "input" && ["", "text", "search", "email", "url", "tel", "number"].includes(sourceType))) {
              state.value = String(source.value || "").slice(0, 500);
            }
          }
          const payload = { role, name: name.slice(0, 200), actions };
          if (Object.keys(state).length > 0) payload.state = state;
          return JSON.stringify(payload);
        }
        """;

    private const string ReadSecrets = """
        () => {
          const items = [];
          const push = (kind, key, value) => items.push({ kind, key: key || "", value: value || "" });
          try {
            for (let i = 0; i < localStorage.length; i++) {
              const key = localStorage.key(i);
              push("storage", key, localStorage.getItem(key));
            }
          } catch { }
          try {
            for (let i = 0; i < sessionStorage.length; i++) {
              const key = sessionStorage.key(i);
              push("storage", key, sessionStorage.getItem(key));
            }
          } catch { }
          document.querySelectorAll('input[type="password"]').forEach(el => push("password", "", el.value || ""));
          return JSON.stringify(items);
        }
        """;

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
    private readonly ConcurrentDictionary<string, LiveElement> _refs = new();
    private IPlaywright? _playwright;
    private Microsoft.Playwright.IBrowser? _browser;
    private BrowserHostPolicy _policy;
    private int _runtimeReady;
    private int _stopped;

    internal Func<Exception?>? CaptureProbe { get; set; }

    public PlaywrightBrowser(
        BrowserOptions options,
        ILoggerFactory? loggerFactory,
        Func<CancellationToken, Task<bool>>? chromiumProbe = null)
    {
        _options = options;
        _logger = loggerFactory?.CreateLogger<PlaywrightBrowser>()
            ?? NullLogger<PlaywrightBrowser>.Instance;
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
        DrainRetiredDrivers();
        await _fixture.DisposeAsync().ConfigureAwait(false);
    }

    public ValueTask<Uri?> GetCurrentUrlAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_sessions.TryGetValue(sessionId, out var session) || !IsAllowed(session, session.Page.Url))
        {
            return new ValueTask<Uri?>(result: null);
        }

        return new ValueTask<Uri?>(Uri.TryCreate(session.Page.Url, UriKind.Absolute, out var uri) ? uri : null);
    }

    public async ValueTask<BrowserOperationResult> NavigateAsync(
        BrowserNavigateRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var interactive = await EnterInteractiveAsync(request.SessionId, cancellationToken).ConfigureAwait(false);
        if (!IsAvailable)
        {
            return Unavailable();
        }

        var operation = request.Operation is "back" or "forward" or "reload" ? request.Operation : "goto";
        if (operation == "goto")
        {
            if (request.Url is null)
            {
                return Result("invalid");
            }

            var leasedOrigins = LeaseOrigins(request.SessionId);
            var decision = leasedOrigins is null
                ? BrowserTargetPolicy.EvaluateDestination(
                    request.Url.AbsoluteUri,
                    _policy.NavigationOrigins,
                    _policy.PolicyMode)
                : BrowserTargetPolicy.EvaluateDestination(
                    request.Url.AbsoluteUri,
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
                            request.Url!.AbsoluteUri,
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

            session.Generation++;
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

    public ValueTask<BrowserOperationResult> SnapshotAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default) =>
        SnapshotCoreAsync(sessionId, null, cancellationToken);

    public ValueTask<BrowserOperationResult> SnapshotAsync(
        Guid sessionId,
        BrowserWaitOptions options,
        CancellationToken cancellationToken = default) =>
        SnapshotCoreAsync(sessionId, options, cancellationToken);

    private async ValueTask<BrowserOperationResult> SnapshotCoreAsync(
        Guid sessionId,
        BrowserWaitOptions? options,
        CancellationToken cancellationToken)
    {
        await using var interactive = await EnterInteractiveAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (!IsAvailable || !_sessions.TryGetValue(sessionId, out var session))
        {
            return Unavailable();
        }

        var entered = false;
        try
        {
            await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            await AdoptOpenWebPageAsync(session, cancellationToken).ConfigureAwait(false);
            if (!IsAllowed(session, session.Page.Url))
            {
                return Result("target_denied");
            }

            if (options?.WaitFor is "navigation" or "role")
            {
                var budget = Math.Clamp(
                    options.TimeoutMs ?? BrowserToolLimits.DefaultObserveTimeoutMs,
                    BrowserToolLimits.MinObserveTimeoutMs,
                    BrowserToolLimits.MaxObserveTimeoutMs);
                if (options.WaitFor == "navigation")
                {
                    await session.Page.WaitForLoadStateAsync(
                            LoadState.Load,
                            new PageWaitForLoadStateOptions { Timeout = budget })
                        .WaitAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                else if (!Enum.TryParse<AriaRole>(options.Role, true, out var role))
                {
                    return Result("invalid");
                }
                else
                {
                    var locator = string.IsNullOrWhiteSpace(options.Name)
                        ? session.Page.GetByRole(role)
                        : session.Page.GetByRole(role, new PageGetByRoleOptions { Name = options.Name });
                    await locator.First.WaitForAsync(new LocatorWaitForOptions
                        {
                            Timeout = budget,
                            State = WaitForSelectorState.Visible
                        })
                        .WaitAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            var settle = options is { WaitFor: "stable" }
                ? BrowserSnapshotSettle.Stable
                : BrowserSnapshotSettle.None;
            return await CaptureWithRetryAsync(
                session,
                sessionId,
                "observe",
                settle,
                options?.TimeoutMs,
                cancellationToken).ConfigureAwait(false);
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
            LogBrowserFailure("observe", "capture", "timeout");
            return Result("timeout");
        }
        catch (PlaywrightException ex)
        {
            return await FailAsync(session, "observe", "capture", ex).ConfigureAwait(false);
        }
        finally
        {
            if (entered)
            {
                session.Gate.Release();
            }
        }
    }

    public async ValueTask<BrowserOperationResult> InteractAsync(
        BrowserInteractionRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var interactive = await EnterInteractiveAsync(request.SessionId, cancellationToken).ConfigureAwait(false);
        if (!IsAvailable)
        {
            return Unavailable();
        }

        var pageScroll = request.Operation == "scroll" && string.IsNullOrEmpty(request.Ref);
        LiveElement? live = null;
        SessionBrowser? session;
        if (pageScroll)
        {
            if (!_sessions.TryGetValue(request.SessionId, out session))
            {
                return Result("stale_reference");
            }
        }
        else
        {
            if (!_refs.TryGetValue(request.Ref, out var found))
            {
                return Result("stale_reference");
            }

            live = found;
            if (live.SessionId != request.SessionId)
            {
                return Result("forbidden");
            }

            if (!_sessions.TryGetValue(request.SessionId, out session) || live.Generation != session.Generation)
            {
                return Result("stale_reference");
            }
        }

        if (!IsAllowed(session, session.Page.Url))
        {
            return Result("target_denied");
        }

        var current = await GetCurrentUrlAsync(request.SessionId, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return Unavailable();
        }

        var decision = BrowserTargetPolicy.EvaluateAct(
            _policy.InteractionMode,
            current.AbsoluteUri,
            _policy.EffectiveInteractionOrigins,
            _policy.PolicyMode);
        if (!decision.Allowed)
        {
            return Result(decision.Code ?? "forbidden");
        }

        var entered = false;
        try
        {
            await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            if (session.Dialog is not null) return Result("dialog_pending");
            if (live is not null && live.Generation != session.Generation)
            {
                return Result("stale_reference");
            }

            if (request.Operation is "fill" or "select" or "press" && request.Value is null)
            {
                return Result("invalid");
            }

            if (request.Operation == "upload" && request.Upload is not { Content.Length: > 0 } && request.Uploads is not { Count: > 0 })
            {
                return Result("invalid");
            }

            ILocator? dragTarget = null;
            if (live is not null)
            {
                if (!await IsAttachedAsync(live.Handle).ConfigureAwait(false))
                {
                    return Result("stale_reference");
                }

                var frameUrl = await live.Handle.EvaluateAsync<string>("el => el.ownerDocument.location.href").WaitAsync(cancellationToken);
                if (!BrowserTargetPolicy.EvaluateAct(_policy.InteractionMode, frameUrl, LeaseOrigins(session) ?? _policy.EffectiveInteractionOrigins, _policy.PolicyMode).Allowed)
                    return Result("target_denied");
                var passwordField = await live.Handle.EvaluateAsync<bool>("el => el.matches('input[type=password]')");
                if (passwordField || live.Actions.Contains("fill_credential")) return Result("unsupported_operation", ["fill_credential"]);

                if (live.Actions.Count > 0 && !ActionOffered(live.Actions, request.Operation))
                {
                    LogBrowserFailure("act", "interaction", "actionNotOffered");
                    return Result("unsupported_operation", live.Actions);
                }
            }

            if (request.Operation == "drag")
            {
                if (live is null
                    || string.IsNullOrEmpty(request.TargetRef)
                    || !_refs.TryGetValue(request.TargetRef, out var target)
                    || target.SessionId != request.SessionId
                    || target.Generation != session.Generation
                    || !await IsAttachedAsync(target.Handle).ConfigureAwait(false))
                {
                    return Result("stale_reference");
                }

                var targetUrl = await target.Handle.EvaluateAsync<string>("el => el.ownerDocument.location.href").WaitAsync(cancellationToken);
                if (!BrowserTargetPolicy.EvaluateAct(_policy.InteractionMode, targetUrl, LeaseOrigins(session) ?? _policy.EffectiveInteractionOrigins, _policy.PolicyMode).Allowed)
                    return Result("target_denied");
                dragTarget = target.Handle;
            }

            var before = session.Page.Url;
            session.DeniedNavigation = false;
            session.PopupCode = null;
            session.TimedOut = false;
            var call = BeginCall(session);
            using var registration = cancellationToken.Register(() => CancelCall(session, call));
            try
            {
                session.DialogSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var action = PerformInteractionAsync(session.Page, live?.Handle, dragTarget, request, cancellationToken);
                if (await Task.WhenAny(action, session.DialogSignal.Task).WaitAsync(cancellationToken) != action)
                {
                    session.PendingAction = action;
                    _ = action.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                    return Result("dialog_pending");
                }
                await action.ConfigureAwait(false);
            }
            catch (PlaywrightException) when (session.PopupCode is not null || session.DeniedNavigation || session.TimedOut)
            {
            }

            if (session.TimedOut)
            {
                LogBrowserFailure("act", "interaction", "timeout");
                return Result("timeout");
            }

            if (session.PopupCode is not null)
            {
                return Result(session.PopupCode);
            }

            await AdoptOpenWebPageAsync(session, cancellationToken).ConfigureAwait(false);

            if (session.DeniedNavigation || !IsAllowed(session, session.Page.Url))
            {
                await RestoreAllowedPageAsync(session, cancellationToken).ConfigureAwait(false);
                return Result("target_denied");
            }

            if (!string.Equals(before, session.Page.Url, StringComparison.Ordinal))
            {
                session.Generation++;
                session.LastAllowedUrl = session.Page.Url;
            }

            var settle = request.Operation is "click" or "doubleClick" or "drag" or "press" or "select" or "check" or "uncheck"
                ? BrowserSnapshotSettle.Automatic
                : BrowserSnapshotSettle.None;
            return await CaptureWithRetryAsync(
                session,
                request.SessionId,
                "act",
                settle,
                timeoutMs: null,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (entered) await FencePageAsync(session, request.SessionId).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (IsCancel(ex, cancellationToken))
        {
            if (entered) await FencePageAsync(session, request.SessionId).ConfigureAwait(false);
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception ex) when (IsTimeout(ex))
        {
            LogBrowserFailure("act", "interaction", "timeout");
            return Result("timeout");
        }
        catch (PlaywrightException ex)
        {
            return await FailAsync(session, "act", "interaction", ex).ConfigureAwait(false);
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

    public async ValueTask<BrowserTabsResult> TabsAsync(BrowserTabsRequest request, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync(new(request.SessionId, "browser.tabs", JsonSerializer.SerializeToElement(new
        { operation = request.Operation, tabRef = request.PageId })), cancellationToken);
        if (result.DataJson is null) return new(result.ErrorCode, [], result.Snapshot);
        using var doc = JsonDocument.Parse(result.DataJson);
        return new(null, doc.RootElement.GetProperty("tabs").EnumerateArray().Select(t => new BrowserPageInfo(
            t.GetProperty("tabRef").GetString()!, t.GetProperty("url").GetString()!, t.GetProperty("active").GetBoolean())).ToArray(), result.Snapshot);
    }

    public async ValueTask<BrowserScreenshotResult> CaptureViewportAsync(
        BrowserScreenshotRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var interactive = await EnterInteractiveAsync(request.SessionId, cancellationToken).ConfigureAwait(false);
        if (!IsAvailable || !_sessions.TryGetValue(request.SessionId, out var session))
        {
            return new BrowserScreenshotResult("provider_unavailable", null, 0);
        }

        var entered = false;
        try
        {
            await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            if (!IsAllowed(session, session.Page.Url))
            {
                return new BrowserScreenshotResult("target_denied", null, 0);
            }

            if (session.Dialog is not null) return new("dialog_pending", null, 0);
            var format = request.Format;
            if (format is not ("png" or "jpeg" or "webp")) return new("invalid", null, 0);
            ILocator? target = null;
            if (request.TargetRef is not null)
            {
                if (!_refs.TryGetValue(request.TargetRef, out var live) || live.SessionId != request.SessionId || live.Generation != session.Generation)
                    return new("stale_reference", null, 0);
                target = live.Handle;
            }
            var secrets = await CollectSecretsAsync(session, cancellationToken);
            var redactions = 0;
            var width = session.Page.ViewportSize?.Width ?? BrowserToolLimits.MaxCaptureWidth;
            var height = session.Page.ViewportSize?.Height ?? BrowserToolLimits.MaxCaptureHeight;
            byte[] png;
            var maskedFrames = new List<IFrame>();
            try
            {
                foreach (var frame in session.Page.Frames)
                {
                    // Mask whole child frames: screenshots cannot prove that cross-origin pixels contain no secrets.
                    if (frame != session.Page.MainFrame && !Allows(session, frame.Url, true)) continue;
                    maskedFrames.Add(frame);
                    redactions += await frame.EvaluateAsync<int>(
                """
                values => {
                  let count = 0;
                  const matches = text => values.some(value => value && text.includes(value));
                  const mask = rect => {
                    if (!rect.width || !rect.height) return;
                    const overlay = document.createElement('div');
                    overlay.setAttribute('data-agent-mask', '1');
                    Object.assign(overlay.style, { position: 'absolute', left: (rect.left + scrollX) + 'px', top: (rect.top + scrollY) + 'px',
                      width: rect.width + 'px', height: rect.height + 'px', background: '#111', zIndex: '2147483647' });
                    document.documentElement.appendChild(overlay); count++;
                  };
                  const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
                  for (let node = walker.nextNode(); node; node = walker.nextNode()) {
                    if (!matches(node.textContent || '')) continue;
                    const range = document.createRange(); range.selectNodeContents(node);
                    for (const rect of range.getClientRects()) mask(rect);
                  }
                  for (const input of document.querySelectorAll('input, textarea, select'))
                    if (matches(input.value || '')) mask(input.getBoundingClientRect());
                  return count;
                }
                """, secrets).WaitAsync(cancellationToken);
                    redactions += await frame.EvaluateAsync<int>(MaskSensitiveScript).WaitAsync(cancellationToken);
                }
                var frameMasks = session.Page.Locator("iframe");
                if (request.FullPage)
                {
                    var dimensions = await session.Page.EvaluateAsync<int[]>("() => [document.documentElement.scrollWidth, document.documentElement.scrollHeight]").WaitAsync(cancellationToken);
                    width = dimensions[0]; height = dimensions[1];
                    if (width > 1920 || height > 12000) return new("capture_too_large", null, redactions, width, height);
                }
                if (target is null)
                    png = await session.Page.ScreenshotAsync(new PageScreenshotOptions
                    {
                        Type = ScreenshotType.Png, FullPage = request.FullPage, Scale = ScreenshotScale.Css,
                        Caret = ScreenshotCaret.Hide, Mask = [frameMasks], Timeout = TimeoutMs()
                    }).WaitAsync(cancellationToken);
                else
                    png = await target.ScreenshotAsync(new LocatorScreenshotOptions
                    { Type = ScreenshotType.Png, Scale = ScreenshotScale.Css, Caret = ScreenshotCaret.Hide,
                      Mask = [session.Page.Locator("input[type=password], input[type=hidden]"), frameMasks], Timeout = TimeoutMs() }).WaitAsync(cancellationToken);
                if (format != "png")
                {
                    using var image = SKImage.FromEncodedData(png);
                    using var output = image.Encode(format == "jpeg" ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Webp, 85);
                    png = output.ToArray();
                }
            }
            finally
            {
                foreach (var frame in maskedFrames)
                    try { await frame.EvaluateAsync(ClearMaskScript).WaitAsync(TimeSpan.FromSeconds(1)); }
                    catch (Exception ex) when (ex is PlaywrightException or TimeoutException) { }
            }

            _logger.LogInformation(
                "browser.screenshot redactions={RedactionCount} bytes={ByteSize} width={Width} height={Height}",
                redactions,
                png.Length,
                width,
                height);
            if (png.Length > BrowserToolLimits.MaxCaptureBytes)
            {
                return new BrowserScreenshotResult("capture_too_large", null, redactions, width, height);
            }

            return new BrowserScreenshotResult(null, png, redactions, width, height, "image/" + format);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsTimeout(ex))
        {
            return new BrowserScreenshotResult("timeout", null, 0);
        }
        catch (PlaywrightException ex)
        {
            var failed = await FailAsync(session, "capture", "capture", ex).ConfigureAwait(false);
            return new BrowserScreenshotResult(failed.ErrorCode, null, 0);
        }
        finally
        {
            if (entered)
            {
                session.Gate.Release();
            }
        }
    }

    internal IBrowserContext? ContextFor(Guid sessionId) =>
        _sessions.TryGetValue(sessionId, out var session) ? session.Context : null;

    public async ValueTask<BrowserCloseResult> CloseAsync(Guid sessionId, CancellationToken cancellationToken = default)
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
            if (Interlocked.Exchange(ref session.RuntimeClosed, 1) == 1)
            {
                return new BrowserCloseResult("already_closed");
            }

            await CloseQuietlyAsync(session.Context).ConfigureAwait(false);
            DropClosed(session);
            return new BrowserCloseResult("closed");
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
            await ClosePersistentAsync(agentInstanceId, cancellationToken).ConfigureAwait(false);
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
                if (Interlocked.Exchange(ref session.RuntimeClosed, 1) == 1)
                {
                    return new BrowserCloseResult("already_closed");
                }

                await CloseQuietlyAsync(session.Context).ConfigureAwait(false);
                DropClosed(session);
                return new BrowserCloseResult("closed");
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
            RemoveRefs(sessionId);
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
            RemoveRefs(sessionId);
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
            RemoveRefs(sessionId);
            _sessionOwners.TryRemove(sessionId, out _);
            if (!session.Persistent)
            {
                await CloseQuietlyAsync(session.Context).ConfigureAwait(false);
            }
        }
    }

    public void BindSession(Guid sessionId, Guid? agentInstanceId) =>
        _sessionOwners[sessionId] = agentInstanceId;

    public async ValueTask<BrowserOperationResult> FillCredentialAsync(Guid sessionId, string reference,
        Func<string, CancellationToken, ValueTask<string>> resolve, CancellationToken ct = default)
    {
        await using var held = await EnterInteractiveAsync(sessionId, ct);
        if (!_refs.TryGetValue(reference, out var live) || live.SessionId != sessionId
            || !_sessions.TryGetValue(sessionId, out var session)) return Result("stale_reference");
        await session.Gate.WaitAsync(ct);
        try
        {
            if (live.Generation != session.Generation || !await IsAttachedAsync(live.Handle)) return Result("stale_reference");
            if (!live.Actions.Contains("fill_credential") || !await live.Handle.EvaluateAsync<bool>("el => el.matches('input[type=password]')"))
                return Result("unsupported_operation");
            if (await ClassifyInterventionAsync(session.Page, ct) != BrowserInterventionKind.None) return Result("user_intervention_required");
            var decision = BrowserTargetPolicy.EvaluateAct(_policy.InteractionMode, session.Page.Url, _policy.EffectiveInteractionOrigins, _policy.PolicyMode);
            if (!decision.Allowed || !IsAllowed(session, session.Page.Url)) return Result("target_denied");
            if (!Uri.TryCreate(await live.Handle.EvaluateAsync<string>("el => el.ownerDocument.location.href").WaitAsync(ct), UriKind.Absolute, out var uri)) return Result("target_denied");
            if (!BrowserTargetPolicy.EvaluateAct(_policy.InteractionMode, uri.AbsoluteUri, _policy.EffectiveInteractionOrigins, _policy.PolicyMode).Allowed) return Result("target_denied");
            var origin = uri.GetLeftPart(UriPartial.Authority);
            var value = await resolve(origin, ct);
            // Register before any effect; reflection after navigation still remains redacted.
            if (!session.ProtectedValues.TryRegister(value)) return Result("user_intervention_required");
            // A page may navigate independently during the resolver await. Recheck before dispatch.
            if (session.Page.Url != uri.AbsoluteUri || live.Generation != session.Generation
                || !await IsAttachedAsync(live.Handle)) return Result("stale_reference");
            if (!await live.Handle.EvaluateAsync<bool>("el => el.matches('input[type=password]')")
                || await ClassifyInterventionAsync(session.Page, ct) != BrowserInterventionKind.None)
                return Result("user_intervention_required");
            await live.Handle.FillAsync(value, new LocatorFillOptions { Timeout = TimeoutMs() }).WaitAsync(ct);
            return await CaptureWithRetryAsync(session, sessionId, "act", BrowserSnapshotSettle.None, null, ct);
        }
        catch (OperationCanceledException) { await FencePageAsync(session, sessionId); throw new OperationCanceledException(ct); }
        catch (PlaywrightException) { return Result("provider_unavailable"); }
        finally { session.Gate.Release(); }
    }

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
            var lease = LeaseOrigins(session);
            if (!ReferenceEquals(page, session.Page))
            {
                if (url.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
                {
                    RememberPage(session, page);
                    await route.ContinueAsync().ConfigureAwait(false);
                    return;
                }

                if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    var popup = BrowserTargetPolicy.EvaluatePopup(
                        url,
                        lease ?? _policy.NavigationOrigins,
                        lease is null ? _policy.PolicyMode : BrowserPolicyMode.Restricted);
                    if (popup.Allowed)
                    {
                        RememberPage(session, page);
                        await route.ContinueAsync().ConfigureAwait(false);
                        return;
                    }

                    if (string.Equals(route.Request.ResourceType, "document", StringComparison.OrdinalIgnoreCase))
                    {
                        session.PopupCode ??= popup.Code ?? "target_denied";
                    }
                }

                await route.AbortAsync().ConfigureAwait(false);
                if (page is not null
                    && string.Equals(route.Request.ResourceType, "document", StringComparison.OrdinalIgnoreCase))
                {
                    ForgetPage(session, page);
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

            var documentNavigation = string.Equals(route.Request.ResourceType, "document", StringComparison.OrdinalIgnoreCase);
            if (!Allows(session, url, documentNavigation))
            {
                if (documentNavigation)
                {
                    session.DeniedNavigation = true;
                }

                await route.AbortAsync().ConfigureAwait(false);
                return;
            }

            BrowserRouteRule? rule;
            lock (session.PopupGate) rule = session.Rules.LastOrDefault(r => string.Equals(r.Url, url, StringComparison.Ordinal));
            if (rule is not null)
            {
                if (rule.Action == "abort") await route.AbortAsync().ConfigureAwait(false);
                else await route.FulfillAsync(new RouteFulfillOptions { Status = rule.Status, ContentType = "text/plain", Body = rule.Body }).ConfigureAwait(false);
                return;
            }

            if (lease is null && _policy.PolicyMode == BrowserPolicyMode.OpenWeb)
            {
                if (IsCancelled(session, session.OperationCall))
                {
                    await route.AbortAsync().ConfigureAwait(false);
                    return;
                }

                await route.ContinueAsync().ConfigureAwait(false);
                return;
            }

            await FulfillWithoutLeavingPolicyAsync(session, route, url, documentNavigation).ConfigureAwait(false);
        }
        catch (PlaywrightException)
        {
            await AbortQuietlyAsync(route).ConfigureAwait(false);
        }
    }

    private async Task FulfillWithoutLeavingPolicyAsync(SessionBrowser session, IRoute route, string url, bool documentNavigation)
    {
        var call = session.OperationCall;
        if (documentNavigation
            && string.Equals(route.Request.Method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            var streamed = await TryStreamAttachmentAsync(session, route, url, call).ConfigureAwait(false);
            if (streamed is not null)
            {
                lock (session.PopupGate)
                {
                    session.StagedDownloads.Add(streamed);
                }

                if (IsCancelled(session, call))
                {
                    await AbortQuietlyAsync(route).ConfigureAwait(false);
                    return;
                }

                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 204,
                    ContentType = "text/plain",
                    Body = ""
                }).ConfigureAwait(false);
                return;
            }
        }

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

        if (IsRedirect(response.Status) && !RedirectStaysAllowed(session, url, response.Headers, documentNavigation))
        {
            if (documentNavigation)
            {
                session.DeniedNavigation = true;
            }

            await AbortQuietlyAsync(route).ConfigureAwait(false);
            return;
        }

        if (IsCancelled(session, call))
        {
            await AbortQuietlyAsync(route).ConfigureAwait(false);
            return;
        }

        if (documentNavigation && TryAttachmentFileName(response.Headers, out var downloadName))
        {
            var staged = DeclaredOverDownloadCap(response.Headers)
                ? new BrowserDownload("download_too_large", downloadName, null, null)
                : await ReadCappedAttachmentAsync(session, route, response.Url, downloadName, call).ConfigureAwait(false);
            lock (session.PopupGate)
            {
                session.StagedDownloads.Add(staged);
            }

            await route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 204,
                ContentType = "text/plain",
                Body = ""
            }).ConfigureAwait(false);
            return;
        }

        await route.FulfillAsync(new RouteFulfillOptions { Response = response }).ConfigureAwait(false);
    }

    private static bool TryAttachmentFileName(IDictionary<string, string> headers, out string fileName)
    {
        fileName = "";
        string? header = null;
        foreach (var pair in headers)
        {
            if (pair.Key.Equals("content-disposition", StringComparison.OrdinalIgnoreCase))
            {
                header = pair.Value;
                break;
            }
        }

        if (header is null || !header.Contains("attachment", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        fileName = "download";
        const string marker = "filename=";
        var index = header.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            var value = header[(index + marker.Length)..].Trim().Trim('"');
            var semi = value.IndexOf(';');
            if (semi >= 0)
            {
                value = value[..semi].Trim().Trim('"');
            }

            fileName = BrowserDownloadPolicy.SanitizeFileName(value);
        }

        return true;
    }

    private static bool DeclaredOverDownloadCap(IDictionary<string, string> headers)
    {
        foreach (var pair in headers)
        {
            if (pair.Key.Equals("content-length", StringComparison.OrdinalIgnoreCase)
                && long.TryParse(pair.Value, out var length))
            {
                return length > BrowserToolLimits.MaxDownloadBytes;
            }
        }

        return false;
    }

    private static readonly HttpClient AttachmentProbe = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        MaxResponseDrainSize = 0
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private async Task<BrowserDownload?> TryStreamAttachmentAsync(SessionBrowser session, IRoute route, string url, int call)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(TimeoutMs()));
        HttpResponseMessage response;
        try
        {
            using var request = AttachmentProbeRequest(route, url);
            response = await AttachmentProbe
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            return null;
        }

        using (response)
        {
            if (IsRedirect((int)response.StatusCode))
            {
                return null;
            }

            if (!TryAttachmentFileName(HeaderMap(response), out var downloadName))
            {
                return null;
            }

            try
            {
                return await ReadBoundedAttachmentAsync(session, response, downloadName, call, timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!IsCancelled(session, call))
            {
                return new BrowserDownload("download_rejected", downloadName, null, null);
            }
        }
    }

    private async Task<BrowserDownload> ReadCappedAttachmentAsync(
        SessionBrowser session,
        IRoute route,
        string url,
        string downloadName,
        int call)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(TimeoutMs()));
        try
        {
            using var request = AttachmentProbeRequest(route, url);
            using var response = await AttachmentProbe
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            return await ReadBoundedAttachmentAsync(session, response, downloadName, call, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            return new BrowserDownload("download_rejected", downloadName, null, null);
        }
    }

    private async Task<BrowserDownload> ReadBoundedAttachmentAsync(
        SessionBrowser session,
        HttpResponseMessage response,
        string downloadName,
        int call,
        CancellationToken cancellationToken)
    {
        if (DeclaredOverDownloadCap(HeaderMap(response)))
        {
            return new BrowserDownload("download_too_large", downloadName, null, null);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        var limit = BrowserToolLimits.MaxDownloadBytes + 1;
        while (true)
        {
            if (IsCancelled(session, call))
            {
                throw new OperationCanceledException(cancellationToken);
            }

            var read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > limit)
            {
                return new BrowserDownload("download_too_large", downloadName, null, null);
            }

            buffer.Write(chunk, 0, read);
        }

        return BrowserDownloadPolicy.Classify(downloadName, buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
    }

    private static HttpRequestMessage AttachmentProbeRequest(IRoute route, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var name in new[] { "cookie", "authorization", "accept" })
        {
            if (route.Request.Headers.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value))
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return request;
    }

    private static Dictionary<string, string> HeaderMap(HttpResponseMessage response)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in response.Headers)
        {
            map[pair.Key] = string.Join(", ", pair.Value);
        }

        foreach (var pair in response.Content.Headers)
        {
            map[pair.Key] = string.Join(", ", pair.Value);
        }

        return map;
    }

    private static bool IsDownloadStart(PlaywrightException exception) =>
        exception.Message.Contains("Download is starting", StringComparison.OrdinalIgnoreCase);

    private bool RedirectStaysAllowed(SessionBrowser session, string requestUrl, IDictionary<string, string> headers, bool documentNavigation)
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
            && Allows(session, resolved.AbsoluteUri, documentNavigation);
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

    private static bool ActionOffered(IReadOnlyList<string> actions, string operation) =>
        actions.Contains(operation, StringComparer.Ordinal)
        || (operation is "doubleClick" or "hover" or "drag" or "scroll"
            && actions.Contains("click", StringComparer.Ordinal));

    private async Task PerformInteractionAsync(
        IPage page,
        ILocator? handle,
        ILocator? dragTarget,
        BrowserInteractionRequest request,
        CancellationToken cancellationToken)
    {
        var timeout = TimeoutMs();
        var operation = request.Operation;
        if (operation == "scroll")
        {
            var delta = request.Delta is > 0 and <= BrowserToolLimits.MaxScrollDelta
                ? request.Delta
                : BrowserToolLimits.DefaultScrollDelta;
            var (dx, dy) = request.Direction switch
            {
                "up" => (0, -delta),
                "left" => (-delta, 0),
                "right" => (delta, 0),
                _ => (0, delta)
            };
            if (handle is not null)
            {
                await handle.HoverAsync(new LocatorHoverOptions { Timeout = timeout })
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            await page.Mouse.WheelAsync(dx, dy).WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (handle is null)
        {
            return;
        }

        if (operation == "doubleClick")
        {
            await handle.DblClickAsync(new LocatorDblClickOptions { Timeout = timeout })
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (operation == "hover")
        {
            await handle.HoverAsync(new LocatorHoverOptions { Timeout = timeout })
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (operation == "drag" && dragTarget is not null)
        {
            await handle.DragToAsync(dragTarget, new LocatorDragToOptions { Timeout = timeout }).WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (operation == "click")
        {
            await handle.ClickAsync(new LocatorClickOptions { Timeout = timeout })
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (operation == "check")
        {
            await handle.CheckAsync(new LocatorCheckOptions { Timeout = timeout, Force = false })
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (operation == "uncheck")
        {
            await handle.UncheckAsync(new LocatorUncheckOptions { Timeout = timeout, Force = false })
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (operation == "upload" && (request.Upload is not null || request.Uploads is { Count: > 0 }))
        {
            var uploads = request.Uploads ?? [request.Upload!];
            await handle.SetInputFilesAsync(uploads.Select(upload => new FilePayload
                { Name = upload.FileName, MimeType = upload.MediaType, Buffer = upload.Content.ToArray() }),
                new LocatorSetInputFilesOptions { Timeout = timeout }).WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Value is null)
        {
            return;
        }

        if (operation == "fill")
        {
            await handle.FillAsync(request.Value, new LocatorFillOptions { Timeout = timeout })
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (operation == "select")
        {
            await handle.SelectOptionAsync(request.Value, new LocatorSelectOptionOptions { Timeout = timeout })
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (operation == "press")
        {
            await handle.PressAsync(request.Value, new LocatorPressOptions { Timeout = timeout })
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<BrowserSnapshot> CaptureAsync(
        SessionBrowser session,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var probe = CaptureProbe?.Invoke();
        if (probe is not null)
        {
            throw probe;
        }

        RemoveRefs(sessionId);
        var title = await session.Page.TitleAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        var text = await session.Page.Locator("body").InnerTextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        var secrets = await CollectSecretsAsync(session, cancellationToken).ConfigureAwait(false);
        title = Redact(title, secrets, session.ProtectedValues);
        text = Redact(text, secrets, session.ProtectedValues);
        var truncated = text.Length > BrowserToolLimits.MaxVisibleTextLength;
        var elements = await CollectElementsAsync(session, sessionId, secrets, cancellationToken).ConfigureAwait(false);
        var intervention = await ClassifyInterventionAsync(session.Page, cancellationToken).ConfigureAwait(false);
        return new BrowserSnapshot(
            Redact(session.Page.Url, secrets, session.ProtectedValues),
            Clip(title, BrowserToolLimits.MaxTitleLength),
            Clip(text, BrowserToolLimits.MaxVisibleTextLength),
            truncated,
            elements,
            intervention,
            SnapshotId: session.SnapshotId,
            TabRef: FindPageId(session),
            Content: Clip(session.SnapshotContent, BrowserToolLimits.MaxSnapshotChars));
    }

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

    private async Task<IReadOnlyList<BrowserElement>> CollectElementsAsync(
        SessionBrowser session, Guid sessionId, IReadOnlyList<string> secrets, CancellationToken ct)
    {
        var elements = new List<BrowserElement>();
        var content = new StringBuilder();
        foreach (var frame in session.Page.Frames)
        {
            if (frame != session.Page.MainFrame && !Allows(session, frame.Url, true)) continue;
            var native = await frame.Locator("body").AriaSnapshotAsync(new LocatorAriaSnapshotOptions
                { Mode = AriaSnapshotMode.Default, Timeout = TimeoutMs() }).WaitAsync(ct).ConfigureAwait(false);
            content.AppendLine(frame == session.Page.MainFrame ? "- document" : "- frame");
            var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var line in native.Split('\n'))
            {
                ct.ThrowIfCancellationRequested();
                var match = System.Text.RegularExpressions.Regex.Match(line,
                    "^\\s*- ([a-z]+)(?: \"((?:[^\"\\\\]|\\\\.)*)\")?");
                if (!match.Success || !Enum.TryParse<AriaRole>(match.Groups[1].Value, true, out var role))
                { content.AppendLine(Redact(line, secrets, session.ProtectedValues)); continue; }
                var name = match.Groups[2].Success ? System.Text.RegularExpressions.Regex.Unescape(match.Groups[2].Value) : null;
                var key = role + "\n" + name;
                ordinals.TryGetValue(key, out var ordinal); ordinals[key] = ordinal + 1;
                var locator = frame.GetByRole(role, new FrameGetByRoleOptions { Name = name, Exact = true }).Nth(ordinal);
                var described = await locator.EvaluateAsync<string>(DescribeElement).WaitAsync(ct).ConfigureAwait(false);
                if (described is null or "null" or "") continue;
                using var doc = JsonDocument.Parse(described);
                var actions = doc.RootElement.GetProperty("actions").EnumerateArray().Select(x => x.GetString()!).ToArray();
                var token = MintToken();
                _refs[token] = new LiveElement(sessionId, session.Generation, locator, actions);
                var safeName = Redact(name ?? doc.RootElement.GetProperty("name").GetString() ?? "", secrets, session.ProtectedValues);
                elements.Add(new BrowserElement(token, match.Groups[1].Value, Clip(safeName, BrowserToolLimits.MaxAccessibleNameLength), actions,
                    ReadControlState(described, safeName, secrets, session.ProtectedValues)));
                content.AppendLine(Redact(line, secrets, session.ProtectedValues) + " [ref=" + token + "]");
            }
            // File inputs are not ARIA nodes in all browser engines. Supplemental refs stay provider-local.
            var files = frame.Locator("input[type=file], input[type=password]");
            for (var index = 0; index < await files.CountAsync().WaitAsync(ct); index++)
            {
                var target = files.Nth(index); var token = MintToken();
                var described = await target.EvaluateAsync<string>(DescribeElement).WaitAsync(ct);
                if (described == "null") continue;
                using var doc = JsonDocument.Parse(described);
                var actions = doc.RootElement.GetProperty("actions").EnumerateArray().Select(a => a.GetString()!).ToArray();
                var name = Redact(doc.RootElement.GetProperty("name").GetString() ?? "File upload", secrets, session.ProtectedValues);
                if (elements.Any(e => e.Name == name && e.Actions.SequenceEqual(actions))) continue;
                _refs[token] = new LiveElement(sessionId, session.Generation, target, actions);
                var role = actions.Contains("upload") ? "button" : "textbox";
                elements.Add(new BrowserElement(token, role, name, actions));
                content.AppendLine("  - " + role + " " + JsonSerializer.Serialize(name) + " [ref=" + token + "]");
            }
        }
        session.SnapshotIndex = elements;
        session.SnapshotContent = content.ToString();
        session.SnapshotId = "snap_" + Guid.NewGuid().ToString("N");
        return elements;
    }

    private async Task<IReadOnlyList<string>> CollectSecretsAsync(SessionBrowser session, CancellationToken cancellationToken)
    {
        var secrets = new List<string>(session.ProtectedValues);
        var cookies = await session.Context.CookiesAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        foreach (var cookie in cookies)
        {
            ConsiderSecret(secrets, "cookie", cookie.Name, cookie.Value);
        }

        foreach (var frame in session.Page.Frames)
        {
            if (frame != session.Page.MainFrame && !Allows(session, frame.Url, true)) continue;
            var storedJson = await frame.EvaluateAsync<string>(ReadSecrets).WaitAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(storedJson)) continue;
            foreach (var item in JsonSerializer.Deserialize<List<BrowserSecretItem>>(storedJson, SecretJson) ?? [])
                ConsiderSecret(secrets, item.Kind, item.Key, item.Value);
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

        RememberPage(session, opened);
        if (LeaseOrigins(session) is null && _policy.PolicyMode == BrowserPolicyMode.OpenWeb)
        {
            session.PendingOpenedPage = opened;
        }
    }

    private async Task AdoptOpenWebPageAsync(SessionBrowser session, CancellationToken cancellationToken)
    {
        if (LeaseOrigins(session) is not null || _policy.PolicyMode != BrowserPolicyMode.OpenWeb)
        {
            return;
        }

        for (var attempt = 0; attempt < 20; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pending = session.PendingOpenedPage;
            if (pending is null)
            {
                RememberAllowedUrl(session);
                return;
            }

            if (pending.IsClosed)
            {
                ClearPending(session, pending);
                RememberAllowedUrl(session);
                return;
            }

            var url = pending.Url;
            if (IsAllowed(url))
            {
                pending.SetDefaultTimeout(TimeoutMs());
                pending.SetDefaultNavigationTimeout(TimeoutMs());
                session.Page = pending;
                ClearPending(session, pending);
                session.Generation++;
                session.LastAllowedUrl = url;
                if (session.PendingOpenedPage is null)
                {
                    return;
                }

                continue;
            }

            if (IsConcreteDeniedUrl(url))
            {
                ClearPending(session, pending);
                await CloseQuietlyAsync(pending).ConfigureAwait(false);
                if (session.PendingOpenedPage is null)
                {
                    return;
                }

                continue;
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ClearPending(SessionBrowser session, IPage pending)
    {
        if (ReferenceEquals(session.PendingOpenedPage, pending))
        {
            session.PendingOpenedPage = null;
        }
    }

    private void RememberAllowedUrl(SessionBrowser session)
    {
        if (IsAllowed(session, session.Page.Url))
        {
            session.LastAllowedUrl = session.Page.Url;
        }
    }

    private bool IsConcreteDeniedUrl(string url) =>
        !string.IsNullOrWhiteSpace(url)
        && !url.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
        && !IsAllowed(url);

    private async Task SettlePopupsAsync(SessionBrowser session)
    {
        if (_policy.PolicyMode == BrowserPolicyMode.OpenWeb)
        {
            return;
        }

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

                var extras = session.Context.Pages.Count(page => !ReferenceEquals(page, session.Page) && !KeepPopup(session, page));
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

    private async Task ClosePopupsAsync(SessionBrowser session)
    {
        foreach (var page in session.Context.Pages.ToArray())
        {
            if (ReferenceEquals(page, session.Page) || KeepPopup(session, page))
            {
                continue;
            }

            ForgetPage(session, page);
            await CloseQuietlyAsync(page).ConfigureAwait(false);
        }
    }

    private bool KeepPopup(SessionBrowser session, IPage page)
    {
        string url;
        try
        {
            if (page.IsClosed)
            {
                return false;
            }

            url = page.Url;
        }
        catch (PlaywrightException)
        {
            return false;
        }

        return url.StartsWith("about:", StringComparison.OrdinalIgnoreCase) || IsAllowed(session, url);
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

    private bool Allows(SessionBrowser session, string url, bool documentNavigation)
    {
        var lease = LeaseOrigins(session);
        if (lease is not null)
        {
            return documentNavigation
                ? BrowserTargetPolicy.EvaluateDestination(url, lease, BrowserPolicyMode.Restricted).Allowed
                : BrowserTargetPolicy.EvaluateResource(url, lease, lease, BrowserPolicyMode.Restricted).Allowed;
        }

        return documentNavigation
            ? BrowserTargetPolicy.EvaluateDestination(url, _policy.NavigationOrigins, _policy.PolicyMode).Allowed
            : BrowserTargetPolicy.EvaluateResource(
                url,
                _policy.NavigationOrigins,
                _policy.EffectiveResourceOrigins,
                _policy.PolicyMode).Allowed;
    }

    private bool CanKeepInterruptedNavigation(SessionBrowser session, PlaywrightException exception)
    {
        if (!BrowserFailureClassifier.IsInterruptedNavigation(exception.Message))
        {
            return false;
        }

        var url = session.Page.Url;
        return Uri.TryCreate(url, UriKind.Absolute, out var landed)
            && landed.Scheme is "http" or "https"
            && IsAllowed(session, url);
    }

    private bool IsAllowed(string url) =>
        BrowserTargetPolicy.EvaluateDestination(url, _policy.NavigationOrigins, _policy.PolicyMode).Allowed;

    private bool IsAllowed(SessionBrowser session, string url)
    {
        var lease = LeaseOrigins(session);
        return lease is null
            ? IsAllowed(url)
            : BrowserTargetPolicy.EvaluateDestination(url, lease, BrowserPolicyMode.Restricted).Allowed;
    }

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

    private static readonly JsonSerializerOptions SecretJson = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static void ConsiderSecret(List<string> secrets, string? kind, string? key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var credential = string.Equals(kind, "password", StringComparison.OrdinalIgnoreCase) || IsCredentialKey(key);
        if (!credential && value.Trim().Length < 4)
        {
            return;
        }

        AddSecret(secrets, value);
    }

    private static bool IsCredentialKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var name = key.ToLowerInvariant();
        return name.Contains("token", StringComparison.Ordinal)
            || name.Contains("auth", StringComparison.Ordinal)
            || name.Contains("secret", StringComparison.Ordinal)
            || name.Contains("password", StringComparison.Ordinal)
            || name.Contains("session", StringComparison.Ordinal)
            || name.Contains("jwt", StringComparison.Ordinal)
            || name.Contains("credential", StringComparison.Ordinal);
    }

    private static void AddSecret(List<string> secrets, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || secrets.Contains(value, StringComparer.Ordinal))
        {
            return;
        }

        secrets.Add(value);
    }

    private static BrowserControlState? ReadControlState(string described, string name, IReadOnlyList<string> secrets, IReadOnlyList<string> protectedValues)
    {
        if (SensitiveControl(name))
        {
            return null;
        }

        using var document = JsonDocument.Parse(described);
        if (!document.RootElement.TryGetProperty("state", out var state)
            || state.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? value = null;
        bool? checkedState = null;
        string? selected = null;
        if (state.TryGetProperty("value", out var valueProperty) && valueProperty.ValueKind == JsonValueKind.String)
        {
            value = Clip(Redact(valueProperty.GetString(), secrets, protectedValues), BrowserToolLimits.MaxFillLength);
        }

        if (state.TryGetProperty("checked", out var checkedProperty)
            && (checkedProperty.ValueKind is JsonValueKind.True or JsonValueKind.False))
        {
            checkedState = checkedProperty.GetBoolean();
        }

        if (state.TryGetProperty("selectedText", out var selectedProperty) && selectedProperty.ValueKind == JsonValueKind.String)
        {
            selected = Clip(Redact(selectedProperty.GetString(), secrets, protectedValues), BrowserToolLimits.MaxFillLength);
        }

        if (value is null && checkedState is null && selected is null)
        {
            return null;
        }

        return new BrowserControlState(value, checkedState, selected);
    }

    private static bool SensitiveControl(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var haystack = name.ToLowerInvariant();
        string[] terms =
        [
            "password",
            "passwd",
            "passcode",
            "secret",
            "token",
            "api key",
            "apikey",
            "access key",
            "private key",
            "client secret",
            "authorization",
            "one-time-code",
            "otp"
        ];
        return terms.Any(term => haystack.Contains(term, StringComparison.Ordinal));
    }

    private static string Redact(string? text, IReadOnlyList<string> secrets, IReadOnlyList<string>? protectedValues = null)
    {
        var current = text ?? string.Empty;
        foreach (var value in protectedValues ?? [])
            if (value.Length > 0) current = current.Replace(value, "[redacted]", StringComparison.Ordinal);
        foreach (var secret in secrets)
        {
            current = secret.Length >= 4
                ? current.Replace(secret, "[redacted]", StringComparison.Ordinal)
                : RedactBounded(current, secret);
        }

        return current;
    }

    private static string RedactBounded(string text, string secret)
    {
        var builder = new StringBuilder(text.Length);
        var index = 0;
        while (index < text.Length)
        {
            var found = text.IndexOf(secret, index, StringComparison.Ordinal);
            if (found < 0)
            {
                builder.Append(text, index, text.Length - index);
                break;
            }

            var beforeOk = found == 0 || !char.IsLetterOrDigit(text[found - 1]);
            var after = found + secret.Length;
            var afterOk = after >= text.Length || !char.IsLetterOrDigit(text[after]);
            builder.Append(text, index, found - index);
            builder.Append(beforeOk && afterOk ? "[redacted]" : secret);
            index = after;
        }

        return builder.ToString();
    }

    private static string Clip(string text, int max) =>
        text.Length <= max ? text : text[..max];

    private const string MaskSensitiveScript = """
        () => {
          const sensitiveTerms = ["password", "passwd", "passcode", "secret", "token", "api key", "apikey", "access key", "private key", "client secret", "authorization", "one-time-code", "otp"];
          const nodes = document.querySelectorAll("input, textarea, select, [data-sensitive]");
          let count = 0;
          for (const node of nodes) {
            const sourceType = (node.getAttribute("type") || "").toLowerCase();
            const autocomplete = (node.getAttribute("autocomplete") || "").toLowerCase();
            const haystack = [node.id, node.getAttribute("name"), autocomplete, node.getAttribute("aria-label"), node.getAttribute("placeholder")]
              .filter(Boolean).join(" ").toLowerCase();
            const sensitive = node.hasAttribute("data-sensitive")
              || sourceType === "password"
              || autocomplete.includes("one-time-code")
              || autocomplete === "username"
              || autocomplete === "current-password"
              || autocomplete.startsWith("cc-")
              || sensitiveTerms.some(term => haystack.includes(term));
            if (!sensitive) continue;
            const rect = node.getBoundingClientRect();
            if (rect.width <= 0 || rect.height <= 0) continue;
            const mask = document.createElement("div");
            mask.setAttribute("data-agent-mask", "1");
            mask.style.position = "absolute";
            mask.style.left = (rect.left + scrollX) + "px";
            mask.style.top = (rect.top + scrollY) + "px";
            mask.style.width = rect.width + "px";
            mask.style.height = rect.height + "px";
            mask.style.background = "#111111";
            mask.style.zIndex = "2147483647";
            document.documentElement.appendChild(mask);
            count += 1;
          }
          return count;
        }
        """;

    private const string ClearMaskScript = """
        () => { for (const node of document.querySelectorAll("[data-agent-mask]")) node.remove(); }
        """;


    private static void RememberOpenPages(SessionBrowser session)
    {
        foreach (var page in session.Context.Pages.ToArray())
        {
            RememberPage(session, page);
        }
    }

    private static List<PageBinding> OpenPages(SessionBrowser session)
    {
        lock (session.PopupGate)
        {
            session.Pages.RemoveAll(item => PageClosed(item.Page));
            return session.Pages.ToList();
        }
    }

    private static IReadOnlyList<BrowserPageInfo> DescribePages(SessionBrowser session)
    {
        var active = session.Page;
        return OpenPages(session)
            .Select(item => new BrowserPageInfo(item.Id, Redact(SafePageUrl(item.Page), [], session.ProtectedValues), ReferenceEquals(item.Page, active)))
            .ToArray();
    }

    private static PageBinding? FindPage(SessionBrowser session, string? pageId)
    {
        if (string.IsNullOrWhiteSpace(pageId))
        {
            return null;
        }

        lock (session.PopupGate)
        {
            return session.Pages.FirstOrDefault(item => string.Equals(item.Id, pageId, StringComparison.Ordinal));
        }
    }

    private static void RememberPage(SessionBrowser session, IPage? page)
    {
        if (page is null || PageClosed(page))
        {
            return;
        }

        var added = false;
        lock (session.PopupGate)
        {
            if (session.Pages.Any(item => ReferenceEquals(item.Page, page)))
            {
                return;
            }

            session.Pages.Add(new PageBinding(MintPageToken(), page));
            added = true;
        }

        if (added)
        {
            page.Dialog += (_, dialog) => { session.Dialog = dialog; session.DialogSignal.TrySetResult(); };
            page.Console += (_, message) => { lock (session.PopupGate) { session.Console.Add(message.Type + ": " + Clip(message.Text, 1000)); if (session.Console.Count > 50) session.Console.RemoveAt(0); } };
            page.Request += (_, request) => { lock (session.PopupGate) { session.Network["req_" + Guid.NewGuid().ToString("N")] = request; if (session.Network.Count > 50) session.Network.Remove(session.Network.Keys.First()); } };
            page.Download += (_, download) =>
            {
                lock (session.PopupGate)
                {
                    session.PendingDownloads.Add(download);
                }
            };
        }
    }

    private static void ForgetPage(SessionBrowser session, IPage page)
    {
        lock (session.PopupGate)
        {
            session.Pages.RemoveAll(item => ReferenceEquals(item.Page, page));
            session.Media.Remove(page);
        }
    }

    private static bool PageClosed(IPage page)
    {
        try
        {
            return page.IsClosed;
        }
        catch (PlaywrightException)
        {
            return true;
        }
    }

    private static string SafePageUrl(IPage page)
    {
        try
        {
            return page.Url;
        }
        catch (PlaywrightException)
        {
            return string.Empty;
        }
    }

    private static string MintPageToken()
    {
        var encoded = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return "pg_" + encoded;
    }

    private static string MintToken()
    {
        var encoded = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return "el_" + encoded;
    }

    private enum BrowserSnapshotSettle
    {
        None,
        Automatic,
        Stable
    }

    private async Task<BrowserOperationResult> CaptureWithRetryAsync(
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
                new BrowserOperationResult(null, await CaptureMarkedAsync(session, sessionId, settled, cancellationToken).ConfigureAwait(false)),
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
                    new BrowserOperationResult(
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

    private async Task<BrowserOperationResult> AttachDownloadsAsync(
        SessionBrowser session,
        BrowserOperationResult result,
        CancellationToken cancellationToken)
    {
        var downloads = await DrainDownloadsAsync(session, cancellationToken).ConfigureAwait(false);
        return downloads.Count == 0 ? result : result with { Downloads = downloads };
    }

    private async Task<IReadOnlyList<BrowserDownload>> DrainDownloadsAsync(
        SessionBrowser session,
        CancellationToken cancellationToken)
    {
        BrowserDownload[] staged;
        IDownload[] pending;
        lock (session.PopupGate)
        {
            staged = session.StagedDownloads.ToArray();
            session.StagedDownloads.Clear();
            pending = session.PendingDownloads.ToArray();
            session.PendingDownloads.Clear();
        }

        if (staged.Length == 0 && pending.Length == 0)
        {
            return [];
        }

        var results = new List<BrowserDownload>(staged.Length + pending.Length);
        results.AddRange(staged);
        foreach (var download in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await ReadDownloadAsync(download, cancellationToken).ConfigureAwait(false));
        }

        return results.Select(download =>
            session.ProtectedValues.Any(value => value.Length > 0 &&
                ((download.FileName?.Contains(value, StringComparison.Ordinal) ?? false)
                 || download.Bytes is not null && download.Bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(value)) >= 0))
                ? new BrowserDownload("download_rejected", null, null, null) : download).ToArray();
    }

    private static async Task<BrowserDownload> ReadDownloadAsync(IDownload download, CancellationToken cancellationToken)
    {
        var fileName = BrowserDownloadPolicy.SanitizeFileName(download.SuggestedFilename);
        try
        {
            await using var stream = await download.CreateReadStreamAsync().ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            var limit = BrowserToolLimits.MaxDownloadBytes + 1;
            while (true)
            {
                var read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (buffer.Length + read > limit)
                {
                    try
                    {
                        await download.CancelAsync().ConfigureAwait(false);
                    }
                    catch (PlaywrightException)
                    {
                    }

                    return new BrowserDownload("download_too_large", fileName, null, null);
                }

                buffer.Write(chunk, 0, read);
            }

            var classified = BrowserDownloadPolicy.Classify(fileName, buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
            try
            {
                await download.DeleteAsync().ConfigureAwait(false);
            }
            catch (PlaywrightException)
            {
            }

            return classified;
        }
        catch (PlaywrightException)
        {
            return new BrowserDownload("download_rejected", fileName, null, null);
        }
    }

    private async Task<BrowserOperationResult> FailAsync(
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

    private static BrowserOperationResult Unavailable() => Result("provider_unavailable");

    private static BrowserOperationResult Result(string code, IReadOnlyList<string>? allowedActions = null) =>
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

    private sealed class BrowserProfileException(string code) : Exception
    {
        public string Code { get; } = code;
    }

    private sealed class SessionBrowser(IBrowserContext context, IPage page)
    {
        public BrowserEnvironment Environment { get; init; } = new(null, "en-US", TimeZoneInfo.Local.Id, false, false, 1);
        public Dictionary<IPage, PageEmulateMediaOptions> Media { get; } = new();
        public string? GeolocationOrigin { get; set; }
        public bool Offline { get; set; }
        public IDialog? Dialog { get; set; }
        public TaskCompletionSource DialogSignal { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? PendingAction { get; set; }
        public List<BrowserRouteRule> Rules { get; } = [];
        public List<string> Console { get; } = [];
        public Dictionary<string, IRequest> Network { get; } = new();
        public string SnapshotId { get; set; } = "";
        public string SnapshotContent { get; set; } = "";
        public IReadOnlyList<BrowserElement> SnapshotIndex { get; set; } = [];
        public ProtectedBrowserValues ProtectedValues { get; } = new();

        public IBrowserContext Context { get; } = context;

        public IPage Page { get; set; } = page;

        public bool Persistent { get; init; }

        public FileStream? ProfileLease { get; init; }

        public IPlaywright? PlaywrightDriver { get; init; }

        public Guid? AgentInstanceId { get; init; }

        public int RuntimeClosed;

        public int Generation { get; set; }

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

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public List<PageBinding> Pages { get; } = [];

        public List<BrowserDownload> StagedDownloads { get; } = [];

        public List<IDownload> PendingDownloads { get; } = [];
    }

    private sealed class PageBinding(string id, IPage page)
    {
        public string Id { get; } = id;

        public IPage Page { get; } = page;
    }

    private sealed record LiveElement(
        Guid SessionId,
        int Generation,
        ILocator Handle,
        IReadOnlyList<string> Actions);

    private sealed record BrowserSecretItem(string? Kind, string? Key, string? Value);

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

    private sealed class UnattendedLease(PlaywrightBrowser owner, Guid agentInstanceId, SemaphoreSlim gate)
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
