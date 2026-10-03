using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;

namespace AgentCore.Infrastructure.Browser;

public sealed class PlaywrightBrowserSession : IBrowserSession, IBrowserSessionLease, IBrowserProfileBinding, IBrowserContextUse, IBrowserRuntimeReadiness, IHostedService
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
          else actions = ["click"];
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

    private const string CollectInteractive = """
        max => {
          const selector = "a, button, input, select, textarea, [role='button'], [role='link'], [role='combobox'], [role='option'], [role='checkbox'], [role='radio'], [role='switch'], [role='textbox'], [role='searchbox'], [role='menuitem'], [role='tab']";
          const isFile = (el) => el.tagName === "INPUT" && (el.getAttribute("type") || "").toLowerCase() === "file";
          const connected = (el) => el instanceof Element && el.isConnected && !el.closest("template")
            && !el.matches(":disabled") && el.getAttribute("aria-disabled") !== "true";
          const visuallyUsable = (el) => {
            if (!connected(el)) return false;
            const type = (el.getAttribute("type") || "").toLowerCase();
            if (el.tagName === "INPUT" && type === "hidden") return false;
            for (let node = el; node instanceof Element; node = node.parentElement) {
              if (node.hasAttribute("hidden") || node.hasAttribute("inert")) return false;
              if (node.getAttribute("aria-hidden") === "true") return false;
              const style = window.getComputedStyle(node);
              if (style.display === "none" || style.opacity === "0") return false;
              if (style.visibility === "hidden" || style.visibility === "collapse") return false;
            }
            const rect = el.getBoundingClientRect();
            return rect.width > 0 && rect.height > 0;
          };
          const usable = (el) => isFile(el) ? connected(el) : visuallyUsable(el);
          const visibleTarget = (el) => {
            if (!el) return null;
            if (visuallyUsable(el)) return el;
            for (const child of el.querySelectorAll("*")) {
              if (visuallyUsable(child)) return child;
            }
            const text = (el.innerText || "").trim();
            const parent = el.parentElement;
            if (text && parent && visuallyUsable(parent) && (parent.innerText || "").trim().length <= 80) return parent;
            return null;
          };
          const isChoice = (el) => {
            const type = (el.getAttribute("type") || "").toLowerCase();
            const role = el.getAttribute("role");
            return type === "checkbox" || type === "radio" || role === "checkbox" || role === "radio" || role === "switch";
          };
          const inVisibleSection = (el) => {
            for (let node = el.parentElement; node instanceof Element; node = node.parentElement) {
              if (node.hasAttribute("hidden") || node.hasAttribute("inert")) return false;
              if (node.getAttribute("aria-hidden") === "true") return false;
              const style = window.getComputedStyle(node);
              if (style.display === "none") return false;
              if (style.visibility === "hidden" || style.visibility === "collapse") return false;
            }
            return true;
          };
          const choiceLabel = (el) => {
            const type = (el.getAttribute("type") || "").toLowerCase();
            const role = el.getAttribute("role");
            const choice = type === "checkbox" || type === "radio" || role === "checkbox" || role === "radio" || role === "switch";
            if (!choice || visuallyUsable(el) || !connected(el)) return null;
            const label = el.labels && el.labels.length > 0 ? el.labels[0] : null;
            return visibleTarget(label);
          };
          const choices = new WeakSet();
          const rank = (el) => {
            if (choices.has(el)) return 1;
            if (isFile(el)) return 0;
            const tag = el.tagName;
            const type = (el.getAttribute("type") || "").toLowerCase();
            if (tag === "LABEL" || tag === "TEXTAREA" || tag === "SELECT") return 1;
            if (tag === "INPUT" && type !== "button" && type !== "submit" && type !== "reset" && type !== "image") return 1;
            if (el.getAttribute("role") === "checkbox" || el.getAttribute("role") === "switch" || type === "checkbox" || type === "radio") return 1;
            const label = ((el.innerText || el.getAttribute("aria-label") || "") + "").toLowerCase();
            if (type === "submit" || label.includes("save")) return 1;
            if (el.closest("nav, aside, [role='navigation']")) return 3;
            return 2;
          };
          const seen = new Set();
          const buckets = [[], [], [], []];
          const admit = (el, choice = false) => {
            if (seen.has(el)) return;
            seen.add(el);
            if (choice) choices.add(el);
            buckets[rank(el)].push(el);
          };
          for (const el of document.querySelectorAll(selector)) {
            if (isChoice(el) && connected(el) && !visuallyUsable(el)) {
              const caption = el.labels && el.labels.length > 0 ? el.labels[0] : null;
              const target = visibleTarget(caption);
              if (target) {
                admit(target, true);
                continue;
              }

              if (inVisibleSection(el)) {
                admit(el, true);
                continue;
              }
            }
            const label = choiceLabel(el);
            if (label) {
              admit(label, true);
              continue;
            }
            if (!usable(el)) continue;
            admit(el);
          }
          const files = buckets[0];
          const fileBudget = Math.min(files.length, Math.min(4, max));
          const found = [];
          const pushUntil = (items, limit) => {
            for (const el of items) {
              if (found.length >= limit) break;
              found.push(el);
            }
          };
          pushUntil(buckets[1], max - fileBudget);
          pushUntil(buckets[2], max - fileBudget);
          pushUntil(buckets[3], max - fileBudget);
          for (let i = 0; i < fileBudget; i++) found.push(files[i]);
          return found;
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
    private readonly ConcurrentDictionary<Guid, string[]> _connectionLeases = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _interactiveWaiters = new();
    private readonly AsyncLocal<Guid?> _unattendedOwner = new();
    private readonly ConcurrentBag<IPlaywright> _retiredDrivers = [];
    private readonly ConcurrentDictionary<string, LiveElement> _refs = new();
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private BrowserHostPolicy _policy;
    private int _runtimeReady;
    private int _stopped;

    internal Func<Exception?>? CaptureProbe { get; set; }

    public PlaywrightBrowserSession(
        BrowserOptions options,
        ILoggerFactory? loggerFactory,
        Func<CancellationToken, Task<bool>>? chromiumProbe = null)
    {
        _options = options;
        _logger = loggerFactory?.CreateLogger<PlaywrightBrowserSession>()
            ?? NullLogger<PlaywrightBrowserSession>.Instance;
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
                BrowserCaptureSettle.Automatic,
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

    public ValueTask<BrowserOperationResult> ObserveAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default) =>
        ObserveCoreAsync(sessionId, null, cancellationToken);

    public ValueTask<BrowserOperationResult> ObserveAsync(
        Guid sessionId,
        BrowserObserveOptions options,
        CancellationToken cancellationToken = default) =>
        ObserveCoreAsync(sessionId, options, cancellationToken);

    private async ValueTask<BrowserOperationResult> ObserveCoreAsync(
        Guid sessionId,
        BrowserObserveOptions? options,
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
                ? BrowserCaptureSettle.Stable
                : BrowserCaptureSettle.None;
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

    public async ValueTask<BrowserOperationResult> ActAsync(
        BrowserActRequest request,
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
            if (live is not null && live.Generation != session.Generation)
            {
                return Result("stale_reference");
            }

            if (request.Operation is "fill" or "select" or "press" && string.IsNullOrEmpty(request.Value))
            {
                return Result("invalid");
            }

            if (request.Operation == "upload" && request.Upload is not { Content.Length: > 0 })
            {
                return Result("invalid");
            }

            IElementHandle? dragTarget = null;
            if (live is not null)
            {
                if (!await IsAttachedAsync(live.Handle).ConfigureAwait(false))
                {
                    return Result("stale_reference");
                }

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
                await PerformActAsync(session.Page, live?.Handle, dragTarget, request, cancellationToken).ConfigureAwait(false);
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
                ? BrowserCaptureSettle.Automatic
                : BrowserCaptureSettle.None;
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

    public async ValueTask<BrowserPagesResult> PagesAsync(
        BrowserPagesRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var interactive = await EnterInteractiveAsync(request.SessionId, cancellationToken).ConfigureAwait(false);
        if (!IsAvailable || !_sessions.TryGetValue(request.SessionId, out var session))
        {
            return new BrowserPagesResult("provider_unavailable", []);
        }

        var entered = false;
        try
        {
            await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            RememberOpenPages(session);
            if (request.Operation == "list")
            {
                return new BrowserPagesResult(null, DescribePages(session));
            }

            if (request.Operation == "adopt")
            {
                var candidate = DescribePages(session).LastOrDefault(page => !page.Active);
                if (candidate is null)
                {
                    return new BrowserPagesResult("no_popup", DescribePages(session));
                }

                return await ActivatePageAsync(session, request.SessionId, candidate.PageId, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (request.Operation is "switch" or "close")
            {
                var binding = FindPage(session, request.PageId);
                if (binding is null || PageClosed(binding.Page))
                {
                    return new BrowserPagesResult("stale_page", DescribePages(session));
                }

                if (request.Operation == "switch")
                {
                    return await ActivatePageAsync(session, request.SessionId, binding.Id, cancellationToken)
                        .ConfigureAwait(false);
                }

                var open = OpenPages(session);
                if (open.Count <= 1)
                {
                    return new BrowserPagesResult("last_page", DescribePages(session));
                }

                var closingActive = ReferenceEquals(binding.Page, session.Page);
                ForgetPage(session, binding.Page);
                await CloseQuietlyAsync(binding.Page).ConfigureAwait(false);
                if (closingActive)
                {
                    var next = OpenPages(session).FirstOrDefault();
                    if (next is not null)
                    {
                        session.Page = next.Page;
                    }
                }

                session.Generation++;
                return new BrowserPagesResult(null, DescribePages(session));
            }

            return new BrowserPagesResult("unsupported_operation", DescribePages(session));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PlaywrightException ex)
        {
            return new BrowserPagesResult((await FailAsync(session, "pages", "lifecycle", ex).ConfigureAwait(false)).ErrorCode, []);
        }
        finally
        {
            if (entered)
            {
                session.Gate.Release();
            }
        }
    }

    public async ValueTask<BrowserCaptureResult> CaptureViewportAsync(
        BrowserCaptureRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var interactive = await EnterInteractiveAsync(request.SessionId, cancellationToken).ConfigureAwait(false);
        if (!IsAvailable || !_sessions.TryGetValue(request.SessionId, out var session))
        {
            return new BrowserCaptureResult("provider_unavailable", null, 0);
        }

        var entered = false;
        try
        {
            await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            if (!IsAllowed(session, session.Page.Url))
            {
                return new BrowserCaptureResult("target_denied", null, 0);
            }

            var redactions = await session.Page.EvaluateAsync<int>(MaskSensitiveScript)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            var size = session.Page.ViewportSize;
            var width = Math.Min(size?.Width ?? BrowserToolLimits.MaxCaptureWidth, BrowserToolLimits.MaxCaptureWidth);
            var height = Math.Min(size?.Height ?? BrowserToolLimits.MaxCaptureHeight, BrowserToolLimits.MaxCaptureHeight);
            byte[] png;
            try
            {
                png = await session.Page.ScreenshotAsync(new PageScreenshotOptions
                    {
                        Type = ScreenshotType.Png,
                        FullPage = false,
                        Scale = ScreenshotScale.Css,
                        Caret = ScreenshotCaret.Hide,
                        Clip = new Clip { X = 0, Y = 0, Width = width, Height = height }
                    })
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                await session.Page.EvaluateAsync(ClearMaskScript).ConfigureAwait(false);
            }

            _logger.LogInformation(
                "browser.capture redactions={RedactionCount} bytes={ByteSize} width={Width} height={Height}",
                redactions,
                png.Length,
                width,
                height);
            if (png.Length > BrowserToolLimits.MaxCaptureBytes)
            {
                return new BrowserCaptureResult("capture_too_large", null, redactions, width, height);
            }

            return new BrowserCaptureResult(null, png, redactions, width, height);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsTimeout(ex))
        {
            return new BrowserCaptureResult("timeout", null, 0);
        }
        catch (PlaywrightException ex)
        {
            var failed = await FailAsync(session, "capture", "capture", ex).ConfigureAwait(false);
            return new BrowserCaptureResult(failed.ErrorCode, null, 0);
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
        _connectionLeases[agentInstanceId] = origins.ToArray();
        return new UnattendedLease(this, agentInstanceId, gate);
    }

    public void AdoptUnattendedFlow(Guid agentInstanceId)
    {
        if (agentInstanceId != Guid.Empty && _connectionLeases.ContainsKey(agentInstanceId))
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
            || !_connectionLeases.ContainsKey(agentInstanceId)
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
            && _connectionLeases.TryGetValue(agentInstanceId, out var origins))
        {
            return origins;
        }

        if (_unattendedOwner.Value is Guid current && _connectionLeases.TryGetValue(current, out var leased))
        {
            return leased;
        }

        return null;
    }

    private string[]? LeaseOrigins(SessionBrowser session) =>
        session.AgentInstanceId is Guid agentInstanceId && _connectionLeases.TryGetValue(agentInstanceId, out var origins)
            ? origins
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
        var context = await browser.NewContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        await context.AddInitScriptAsync(BrowserPageSettle.InitScript).WaitAsync(cancellationToken).ConfigureAwait(false);
        var page = await context.NewPageAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        page.SetDefaultTimeout(TimeoutMs());
        page.SetDefaultNavigationTimeout(TimeoutMs());
        var session = new SessionBrowser(context, page);
        RememberPage(session, page);
        context.Close += (_, _) => ForgetClosed(session);
        context.Page += (_, opened) => OnContextPage(session, opened);
        await context.RouteAsync("**/*", route => RouteAsync(session, route)).ConfigureAwait(false);
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
                var options = new BrowserTypeLaunchPersistentContextOptions
                {
                    Headless = _options.Headless,
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
                    AgentInstanceId = agentInstanceId
                };
                RememberPage(session, page);
                context.Close += (_, _) => ForgetClosed(session);
                context.Page += (_, opened) => OnContextPage(session, opened);
                await context.RouteAsync("**/*", route => RouteAsync(session, route)).ConfigureAwait(false);
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
                var options = new BrowserTypeLaunchOptions
                {
                    Headless = _options.Headless,
                    Args = ["--disable-popup-blocking"]
                };
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

        await route.FulfillAsync(new RouteFulfillOptions { Response = response }).ConfigureAwait(false);
    }

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

    private async Task PerformActAsync(
        IPage page,
        IElementHandle? handle,
        IElementHandle? dragTarget,
        BrowserActRequest request,
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
                await handle.HoverAsync(new ElementHandleHoverOptions { Timeout = timeout })
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
            await handle.DblClickAsync(new ElementHandleDblClickOptions { Timeout = timeout })
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (operation == "hover")
        {
            await handle.HoverAsync(new ElementHandleHoverOptions { Timeout = timeout })
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (operation == "drag" && dragTarget is not null)
        {
            var sourceBox = await handle.BoundingBoxAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            var targetBox = await dragTarget.BoundingBoxAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            if (sourceBox is null || targetBox is null)
            {
                return;
            }

            await page.Mouse.MoveAsync(sourceBox.X + (sourceBox.Width / 2), sourceBox.Y + (sourceBox.Height / 2))
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            await page.Mouse.DownAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            await page.Mouse.MoveAsync(targetBox.X + (targetBox.Width / 2), targetBox.Y + (targetBox.Height / 2))
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            await page.Mouse.UpAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (operation == "click")
        {
            await handle.ClickAsync(new ElementHandleClickOptions { Timeout = timeout })
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (operation == "check")
        {
            await handle.CheckAsync(new ElementHandleCheckOptions { Timeout = timeout, Force = true })
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (operation == "uncheck")
        {
            await handle.UncheckAsync(new ElementHandleUncheckOptions { Timeout = timeout, Force = true })
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (operation == "upload" && request.Upload is { Content.Length: > 0 } upload)
        {
            await handle.SetInputFilesAsync(
                    new FilePayload
                    {
                        Name = upload.FileName,
                        MimeType = upload.MediaType,
                        Buffer = upload.Content.ToArray()
                    },
                    new ElementHandleSetInputFilesOptions { Timeout = timeout })
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
        var probe = CaptureProbe?.Invoke();
        if (probe is not null)
        {
            throw probe;
        }

        RemoveRefs(sessionId);
        var title = await session.Page.TitleAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        var text = await session.Page.Locator("body").InnerTextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        var secrets = await CollectSecretsAsync(session, cancellationToken).ConfigureAwait(false);
        title = Redact(title, secrets);
        text = Redact(text, secrets);
        var truncated = text.Length > BrowserToolLimits.MaxVisibleTextLength;
        var elements = await CollectElementsAsync(session, sessionId, secrets, cancellationToken).ConfigureAwait(false);
        var intervention = await ClassifyInterventionAsync(session.Page, cancellationToken).ConfigureAwait(false);
        return new BrowserObservation(
            session.Page.Url,
            Clip(title, BrowserToolLimits.MaxTitleLength),
            Clip(text, BrowserToolLimits.MaxVisibleTextLength),
            truncated,
            elements,
            intervention);
    }

    private async Task<BrowserObservation> CaptureMarkedAsync(
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
          if (registration && email) return "registration";
          return "authentication";
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
            return BrowserInterventionKind.None;
        }
    }

    private async Task<IReadOnlyList<BrowserElement>> CollectElementsAsync(
        SessionBrowser session,
        Guid sessionId,
        IReadOnlyList<string> secrets,
        CancellationToken cancellationToken)
    {
        var list = await session.Page.EvaluateHandleAsync(CollectInteractive, BrowserToolLimits.MaxElements)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        var properties = await list.GetPropertiesAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        var elements = new List<BrowserElement>();
        foreach (var property in properties)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (elements.Count >= BrowserToolLimits.MaxElements)
            {
                break;
            }

            var handle = property.Value.AsElement();
            if (handle is null)
            {
                continue;
            }

            var described = await handle.EvaluateAsync<string>(DescribeElement).WaitAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(described) || described == "null")
            {
                continue;
            }

            var role = "generic";
            var name = string.Empty;
            var actions = new List<string>();
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

                if (document.RootElement.TryGetProperty("actions", out var actionProperty)
                    && actionProperty.ValueKind == JsonValueKind.Array)
                {
                    foreach (var action in actionProperty.EnumerateArray())
                    {
                        if (action.ValueKind == JsonValueKind.String
                            && action.GetString() is { Length: > 0 } value
                            && BrowserToolLimits.Operations.Contains(value, StringComparer.Ordinal))
                        {
                            actions.Add(value);
                        }
                    }
                }
            }

            if (actions.Count == 0)
            {
                continue;
            }

            var token = MintToken();
            _refs[token] = new LiveElement(sessionId, session.Generation, handle, actions);
            elements.Add(new BrowserElement(
                token,
                Clip(Redact(role, secrets), BrowserToolLimits.MaxRoleLength),
                Clip(Redact(name, secrets), BrowserToolLimits.MaxAccessibleNameLength),
                actions,
                ReadControlState(described, name, secrets)));
        }

        return elements;
    }

    private async Task<IReadOnlyList<string>> CollectSecretsAsync(SessionBrowser session, CancellationToken cancellationToken)
    {
        var secrets = new List<string>();
        var cookies = await session.Context.CookiesAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        foreach (var cookie in cookies)
        {
            ConsiderSecret(secrets, "cookie", cookie.Name, cookie.Value);
        }

        var storedJson = await session.Page.EvaluateAsync<string>(ReadSecrets).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(storedJson))
        {
            var stored = JsonSerializer.Deserialize<List<BrowserSecretItem>>(storedJson, SecretJson) ?? [];
            foreach (var item in stored)
            {
                ConsiderSecret(secrets, item.Kind, item.Key, item.Value);
            }
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

    private static BrowserControlState? ReadControlState(string described, string name, IReadOnlyList<string> secrets)
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
            value = Clip(Redact(valueProperty.GetString(), secrets), BrowserToolLimits.MaxFillLength);
        }

        if (state.TryGetProperty("checked", out var checkedProperty)
            && (checkedProperty.ValueKind is JsonValueKind.True or JsonValueKind.False))
        {
            checkedState = checkedProperty.GetBoolean();
        }

        if (state.TryGetProperty("selectedText", out var selectedProperty) && selectedProperty.ValueKind == JsonValueKind.String)
        {
            selected = Clip(Redact(selectedProperty.GetString(), secrets), BrowserToolLimits.MaxFillLength);
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

    private static string Redact(string? text, IReadOnlyList<string> secrets)
    {
        var current = text ?? string.Empty;
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
          const nodes = document.querySelectorAll("input[type='password'], input[autocomplete='username'], input[autocomplete='current-password'], input[autocomplete^='cc-'], [data-sensitive]");
          let count = 0;
          for (const node of nodes) {
            const rect = node.getBoundingClientRect();
            if (rect.width <= 0 || rect.height <= 0) continue;
            const mask = document.createElement("div");
            mask.setAttribute("data-agent-mask", "1");
            mask.style.position = "fixed";
            mask.style.left = rect.left + "px";
            mask.style.top = rect.top + "px";
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

    private async Task<BrowserPagesResult> ActivatePageAsync(
        SessionBrowser session,
        Guid sessionId,
        string pageId,
        CancellationToken cancellationToken)
    {
        var binding = FindPage(session, pageId);
        if (binding is null || PageClosed(binding.Page))
        {
            return new BrowserPagesResult("stale_page", DescribePages(session));
        }

        session.Page = binding.Page;
        session.Generation++;
        session.LastAllowedUrl = session.Page.Url;
        var captured = await CaptureWithRetryAsync(
            session,
            sessionId,
            "pages",
            BrowserCaptureSettle.None,
            timeoutMs: null,
            cancellationToken).ConfigureAwait(false);
        return new BrowserPagesResult(captured.ErrorCode, DescribePages(session), captured.Observation);
    }

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
            .Select(item => new BrowserPageInfo(item.Id, SafePageUrl(item.Page), ReferenceEquals(item.Page, active)))
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

        lock (session.PopupGate)
        {
            if (session.Pages.Any(item => ReferenceEquals(item.Page, page)))
            {
                return;
            }

            session.Pages.Add(new PageBinding(MintPageToken(), page));
        }
    }

    private static void ForgetPage(SessionBrowser session, IPage page)
    {
        lock (session.PopupGate)
        {
            session.Pages.RemoveAll(item => ReferenceEquals(item.Page, page));
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

    private enum BrowserCaptureSettle
    {
        None,
        Automatic,
        Stable
    }

    private async Task<BrowserOperationResult> CaptureWithRetryAsync(
        SessionBrowser session,
        Guid sessionId,
        string operation,
        BrowserCaptureSettle settle,
        int? timeoutMs,
        CancellationToken cancellationToken)
    {
        bool? settled = null;
        try
        {
            if (settle != BrowserCaptureSettle.None)
            {
                var budget = settle == BrowserCaptureSettle.Automatic
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
                    settle == BrowserCaptureSettle.Stable ? "stable" : "automatic",
                    reached,
                    Environment.TickCount64 - started);
            }

            return new BrowserOperationResult(null, await CaptureMarkedAsync(session, sessionId, settled, cancellationToken).ConfigureAwait(false));
        }
        catch (PlaywrightException ex) when (BrowserFailureClassifier.IsTransientCapture(ex.Message))
        {
            LogBrowserFailure(operation, "capture", BrowserFailureClassifier.Classify(ex.Message).Reason);
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
                return new BrowserOperationResult(
                    null,
                    await CaptureMarkedAsync(session, sessionId, settled, cancellationToken).ConfigureAwait(false));
            }
            catch (PlaywrightException retry)
            {
                return await FailAsync(session, operation, "capture", retry).ConfigureAwait(false);
            }
        }
        catch (PlaywrightException ex)
        {
            return await FailAsync(session, operation, "capture", ex).ConfigureAwait(false);
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
    }

    private sealed class PageBinding(string id, IPage page)
    {
        public string Id { get; } = id;

        public IPage Page { get; } = page;
    }

    private sealed record LiveElement(
        Guid SessionId,
        int Generation,
        IElementHandle Handle,
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

    private sealed class UnattendedLease(PlaywrightBrowserSession owner, Guid agentInstanceId, SemaphoreSlim gate)
        : IAsyncDisposable
    {
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return ValueTask.CompletedTask;
            }

            owner._connectionLeases.TryRemove(agentInstanceId, out _);
            if (owner._unattendedOwner.Value == agentInstanceId)
            {
                owner._unattendedOwner.Value = null;
            }

            gate.Release();
            return ValueTask.CompletedTask;
        }
    }
}
