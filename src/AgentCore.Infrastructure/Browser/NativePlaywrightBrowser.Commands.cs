using System.Text.Json;
using System.Text.RegularExpressions;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using Microsoft.Playwright;

namespace AgentCore.Infrastructure.Browser;

public sealed partial class NativePlaywrightBrowser
{
    // Evaluation, raw storage export, trace and video cannot guarantee secret masking in this host.
    public BrowserProviderDescriptor Provider { get; } = new("playwright", "Playwright", new HashSet<BrowserFeature>
    {
        BrowserFeature.Navigate, BrowserFeature.Snapshot, BrowserFeature.Find, BrowserFeature.Click,
        BrowserFeature.Hover, BrowserFeature.Drag, BrowserFeature.Drop, BrowserFeature.Type, BrowserFeature.FillForm,
        BrowserFeature.SelectOption, BrowserFeature.PressKey, BrowserFeature.Upload, BrowserFeature.FillCredential,
        BrowserFeature.Scroll, BrowserFeature.Wait, BrowserFeature.Tabs, BrowserFeature.Dialog, BrowserFeature.Close, BrowserFeature.Screenshot,
        BrowserFeature.NetworkControl, BrowserFeature.Storage, BrowserFeature.Resize, BrowserFeature.Console, BrowserFeature.NetworkInspect, BrowserFeature.Testing,
        BrowserFeature.VisionMouse, BrowserFeature.Highlight, BrowserFeature.Media, BrowserFeature.Configuration, BrowserFeature.Geolocation
    }) { Engine = "chromium" };

    private static string? FindPageId(SessionBrowser session) => OpenPages(session).FirstOrDefault(x => ReferenceEquals(x.Page, session.Page))?.Id;
    public async ValueTask<BrowserResult> ExecuteAsync(BrowserRequest command, CancellationToken cancellationToken = default)
    {
        if (!BrowserToolCatalog.TryGet(BrowserToolArguments.ToolName(command.Operation), out var metadata) || !Provider.Supports(metadata.Feature)) return new("unsupported_operation");
        if (command.Operation == BrowserOperation.Navigate) return await NavigateAsync(command, cancellationToken);
        if (command.Operation == BrowserOperation.Close)
        {
            var closed = await CloseAsync(command.SessionId, cancellationToken);
            return closed.Status is "closed" or "already_closed" ? new(null, Status: closed.Status,
                DataJson: JsonSerializer.Serialize(new { status = closed.Status })) : new(closed.Status, Status: closed.Status);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAvailable) return command.Operation == BrowserOperation.GetConfig ? Configuration(null) : new("provider_unavailable");
        if (!_sessions.TryGetValue(command.SessionId, out var session))
            return command.Operation == BrowserOperation.GetConfig ? Configuration(null) : new("provider_unavailable");
        if (command.Command is BrowserScreenshot captureArgs)
        {
            var capture = await CaptureViewportAsync(new BrowserScreenshotRequest(command.SessionId, captureArgs.Format,
                captureArgs.FullPage, captureArgs.Target), cancellationToken);
            return new(capture.ErrorCode, Bytes: capture.Png, ContentType: capture.ContentType, FileName: "screenshot." + capture.ContentType.Split('/')[1], RedactionCount: capture.RedactionCount, Width: capture.Width, Height: capture.Height);
        }
        await using var interactive = await EnterInteractiveAsync(command.SessionId, cancellationToken).ConfigureAwait(false);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        // One deadline spans validation, provider reads, waits, action and final capture.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(TimeoutMs()));
        var ct = deadline.Token;
        var call = BeginCall(session);
        using var cancel = ct.Register(() => CancelCall(session, call));
        session.DeniedNavigation = false; session.PopupCode = null; session.TimedOut = false;
        Task? activeAction = null;
        var effectAttempted = false;
        var effectConfirmed = false;
        session.DialogSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        BrowserResult Data(object value) => new(null, DataJson: JsonSerializer.Serialize(value));
        try
        {
            var result = await Dispatch();
            return result with { EffectAttempted = result.EffectAttempted || effectAttempted,
                EffectConfirmedBySdk = result.EffectConfirmedBySdk || effectConfirmed };
        }
        catch (BrowserDialogPendingException) { return new("dialog_pending", EffectAttempted: effectAttempted); }
        catch (BrowserTargetException ex) { return new(ex.Code, EffectAttempted: effectAttempted, EffectConfirmedBySdk: effectConfirmed); }
        catch (BrowserTargetDeniedException) { return new("target_denied", EffectAttempted: effectAttempted, EffectConfirmedBySdk: effectConfirmed); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await FenceActionAsync();
            throw new OperationCanceledException(cancellationToken);
        }
        catch (OperationCanceledException) { await FenceActionAsync(); return new("timeout", EffectAttempted: effectAttempted, EffectConfirmedBySdk: effectConfirmed); }
        catch (RegexMatchTimeoutException) { return new("timeout", EffectAttempted: effectAttempted, EffectConfirmedBySdk: effectConfirmed); }
        catch (TimeoutException) { return new("timeout", EffectAttempted: effectAttempted, EffectConfirmedBySdk: effectConfirmed); }
        catch (PlaywrightException ex) { return new((await FailAsync(session, metadata.Feature.ToString(), "interaction", ex)).ErrorCode, EffectAttempted: effectAttempted, EffectConfirmedBySdk: effectConfirmed); }
        finally { session.Gate.Release(); }

        async Task<BrowserResult> Dispatch()
        {
            if (session.Dialog?.Page?.IsClosed == true)
            { session.Dialog = null; session.PendingAction = null; }
            if (command.Operation == BrowserOperation.GetConfig) return Configuration(session);
            if (session.Dialog is not null && command.Operation != BrowserOperation.Dialog) return new("dialog_pending");
            if (command.Operation is not (BrowserOperation.Tabs or BrowserOperation.Dialog)) await AdoptOpenWebPageAsync(session, ct);
            if (!IsAllowed(session, session.Page.Url)) return new("target_denied");
            var readsOnly = metadata.Effect == ToolEffect.ReadOnly || command.Command is BrowserTabs { Operation: "list" }
                || command.Command is BrowserDialog { Operation: "inspect" };
            if (!readsOnly && !BrowserTargetPolicy.EvaluateAct(_policy.InteractionMode,
                session.Page.Url, _policy.EffectiveInteractionOrigins, _policy.PolicyMode).Allowed) return new("forbidden");
            switch (command.Command)
            {
                case BrowserSetGeolocation args:
                    return await GeolocationAsync(session, args, ct);
                case BrowserObserve args:
                    {
                        var scope = args.Target is null ? session.Page.Locator("body") : await Target(args.Target);
                        return new(null, await ObserveAsync(session, command.SessionId, scope!, args.Depth, args.Target, ct, args.Boxes));
                    }
                case BrowserFind args:
                    return await FindAsync(session, args, ct);
                case BrowserUploadCommand args:
                    {
                        var target = await Target(args.Target);
                        if (target is null) return new("target_missing");
                        if (!await Ordinary(target)) return new("forbidden");
                        if (args.Uploads is not { Count: > 0 and <= 8 } uploads) return new("invalid");
                        await Action(target.SetInputFilesAsync(uploads.Select(u => new FilePayload
                        { Name = u.FileName, MimeType = u.MediaType, Buffer = u.Content.ToArray() }), new() { Timeout = TimeoutMs() }));
                        break;
                    }
                case BrowserHover args:
                    {
                        var target = await Target(args.Target);
                        if (target is null) return new("target_missing");
                        if (!await Ordinary(target)) return new("forbidden");
                        await Action(target.HoverAsync(new() { Timeout = TimeoutMs() })); break;
                    }
                case BrowserDrag args:
                    {
                        var source = await Target(args.Target); var destination = await Target(args.Destination);
                        if (source is null || destination is null) return new("target_missing");
                        if (!await Ordinary(source) || !await Ordinary(destination)) return new("forbidden");
                        await Action(source.DragToAsync(destination, new() { Timeout = TimeoutMs() })); break;
                    }
                case BrowserTabs args:
                    {
                        RememberOpenPages(session);
                        var operation = args.Operation;
                        if (operation is not ("list" or "new" or "select" or "close")) return new("invalid");
                        if (operation == "list")
                        {
                            var pages = await DescribePagesAsync(session, ct);
                            return Data(new { tabs = pages.Select(t => new { tabRef = t.PageId, url = t.Url, active = t.Active }) }) with { Pages = pages };
                        }
                        if (operation == "new")
                        {
                            var url = args.Url;
                            if (!BrowserTargetPolicy.EvaluateDestination(url, LeaseOrigins(session) ?? _policy.NavigationOrigins, _policy.PolicyMode).Allowed) return new("target_denied");
                            // Page creation can finish late; close the context if cancellation wins that race.
                            effectAttempted = true; effectConfirmed = false;
                            var creation = session.Context.NewPageAsync();
                            await MutateContextAsync(session, creation, ct);
                            var page = await creation; RememberPage(session, page);
                            var navigation = page.GotoAsync(url!, new PageGotoOptions { Timeout = TimeoutMs(), WaitUntil = WaitUntilState.DOMContentLoaded });
                            try { await navigation.WaitAsync(ct); effectConfirmed = true; }
                            catch (Exception ex) when (ex is OperationCanceledException or PlaywrightException)
                            {
                                await CloseQuietlyAsync(page); ForgetPage(session, page);
                                _ = navigation.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                                throw;
                            }
                            if (!IsAllowed(session, page.Url)) { await page.CloseAsync().WaitAsync(ct); return new("target_denied"); }
                            session.Page = page; session.LastAllowedUrl = page.Url; AdvanceGeneration(session);
                            return new(null, await CaptureAsync(session, command.SessionId, ct));
                        }
                        var binding = FindPage(session, args.TabRef);
                        if (binding is null || binding.Page.IsClosed) return new("stale_tab");
                        if (!IsAllowed(session, binding.Page.Url)) return new("target_denied");
                        if (operation == "close")
                        {
                            if (OpenPages(session).Count == 1) return new("last_tab");
                            ForgetPage(session, binding.Page); await Action(binding.Page.CloseAsync());
                            if (ReferenceEquals(session.Page, binding.Page)) session.Page = OpenPages(session).First().Page;
                        }
                        else if (operation == "select")
                        {
                            // Bind cancellation fencing to the page being activated, not the previous tab.
                            session.Page = binding.Page; session.LastAllowedUrl = binding.Page.Url;
                            await Action(ActivateTabProbe is { } activate ? activate(binding.Page) : binding.Page.BringToFrontAsync());
                        }
                        else return new("invalid");
                        AdvanceGeneration(session); return new(null, await CaptureAsync(session, command.SessionId, ct));
                    }
                case BrowserDialog args:
                    {
                        var dialog = session.Dialog;
                        if (dialog is null) return new("dialog_missing");
                        // A modal blocks page JS, including the live storage/password secret collector.
                        // Mask the entire untrusted message rather than trusting stale pre-dialog evidence.
                        if (args.Operation == "inspect") return Data(new { kind = dialog.Type, message = "[redacted]", messageRedacted = true });
                        effectAttempted = true; effectConfirmed = false;
                        activeAction = DialogResolutionProbe is { } resolve ? resolve(dialog)
                            : args.Operation == "accept" ? dialog.AcceptAsync(args.PromptText) : dialog.DismissAsync();
                        try { await activeAction.WaitAsync(ct); }
                        catch (PlaywrightException ex) when (ex.Message.Contains("already handled", StringComparison.OrdinalIgnoreCase))
                        {
                            if (ReferenceEquals(session.Dialog, dialog)) session.Dialog = null;
                            return new("dialog_missing");
                        }
                        activeAction = null; effectConfirmed = true;
                        if (ReferenceEquals(session.Dialog, dialog)) session.Dialog = null;
                        if (session.PendingAction is { } pending)
                        {
                            activeAction = pending;
                            if (await Task.WhenAny(pending, session.DialogSignal.Task).WaitAsync(ct) != pending)
                            {
                                activeAction = null;
                                return new("dialog_pending");
                            }
                            await pending.WaitAsync(ct);
                            activeAction = null;
                            session.PendingAction = null;
                        }
                        if (session.Dialog is not null) return new("dialog_pending");
                        session.DialogSignal = new(TaskCreationOptions.RunContinuationsAsynchronously); return new(null, await CaptureAsync(session, command.SessionId, ct));
                    }
                case BrowserWaitFor args:
                    {
                        var condition = args.Condition; var text = args.Text;
                        if (condition == "stable")
                        {
                            var settled = await BrowserPageSettle.WaitAsync(session.Page, args.TimeoutMs, ct);
                            return new(null, (await CaptureAsync(session, command.SessionId, ct)) with { Settled = settled });
                        }
                        if (condition is "text" or "textGone")
                        {
                            if (text is null) return new("invalid");
                            // A hidden first match must neither block an existing visible match
                            // nor prove that every matching text has disappeared.
                            await session.Page.GetByText(text, new PageGetByTextOptions { Exact = false })
                                .Filter(new() { Visible = true }).First.WaitForAsync(new LocatorWaitForOptions
                            { State = condition == "text" ? WaitForSelectorState.Visible : WaitForSelectorState.Hidden, Timeout = args.TimeoutMs }).WaitAsync(ct);
                        }
                        else if (condition == "target")
                        {
                            var state = args.State ?? "visible";
                            var target = await Target(args.Target, state is "hidden" or "detached"); if (target is null) return new("target_missing");
                            if (state is "enabled" or "disabled")
                            {
                                await Assertions.Expect(target).ToBeEnabledAsync(new LocatorAssertionsToBeEnabledOptions { Enabled = state == "enabled", Timeout = args.TimeoutMs }).WaitAsync(ct);
                            }
                            else if (Enum.TryParse<WaitForSelectorState>(state, true, out var parsed))
                                await target.WaitForAsync(new LocatorWaitForOptions { State = parsed, Timeout = args.TimeoutMs }).WaitAsync(ct);
                            else return new("invalid");
                        }
                        else if (condition == "url")
                        {
                            var url = args.Url; if (url is null) return new("invalid");
                            await session.Page.WaitForURLAsync(url, new PageWaitForURLOptions { Timeout = args.TimeoutMs }).WaitAsync(ct);
                        }
                        else if (condition == "load") await session.Page.WaitForLoadStateAsync(args.State == "load" ? LoadState.Load : LoadState.DOMContentLoaded,
                            new PageWaitForLoadStateOptions { Timeout = args.TimeoutMs }).WaitAsync(ct);
                        else return new("invalid");
                        return new(null, await CaptureAsync(session, command.SessionId, ct));
                    }
                case BrowserScroll args:
                    if (args.Target is { } scrollRef)
                    {
                        var target = await Target(scrollRef);
                        if (target is null) return new("target_missing");
                        if (!await Ordinary(target)) return new("forbidden");
                        await target.HoverAsync(new() { Timeout = TimeoutMs() }).WaitAsync(ct);
                    }
                    await Action(session.Page.Mouse.WheelAsync(args.DeltaX, args.DeltaY));
                    break;
                case BrowserResize args:
                    await Action(ResizeProbe is { } resizeProbe
                        ? resizeProbe(session.Page, args.Width, args.Height)
                        : session.Page.SetViewportSizeAsync(args.Width, args.Height)); break;
                case BrowserFillForm args:
                    {
                        var fields = new List<BrowserFormField>();
                        foreach (var field in args.Fields ?? [])
                        {
                            if ((field.Value is not null) == (field.Checked is not null)) return new("invalid");
                            var target = await Target(field.Target); if (target is null) return new("target_missing");
                            if (!await Ordinary(target)) return new("forbidden");
                            fields.Add(field);
                        }
                        foreach (var field in fields)
                        {
                            // Earlier input handlers may rerender, duplicate or protect a later field.
                            // Retain all-field preflight and recheck the live authority before each effect.
                            var target = await Target(field.Target); if (target is null) return new("target_missing");
                            if (!await Ordinary(target)) return new("forbidden");
                            if (field.Checked is bool check) await Action(target.SetCheckedAsync(check, new LocatorSetCheckedOptions { Timeout = TimeoutMs() }));
                            else if (field.Value is { } value) await Action(target.FillAsync(value, new LocatorFillOptions { Timeout = TimeoutMs() }));
                            else return new("invalid");
                        }
                        break;
                    }
                case BrowserPressKey args:
                    {
                        var key = args.Key; if (key is null || !Regex.IsMatch(key, "^[A-Za-z0-9+_-]{1,80}$")) return new("invalid");
                        if (args.Target is { } reference)
                        { var target = await Target(reference); if (target is null) return new("target_missing"); if (!await Ordinary(target)) return new("forbidden"); await Action(target.PressAsync(key, new LocatorPressOptions { Timeout = TimeoutMs() })); }
                        else
                        {
                            if (!await session.Page.EvaluateAsync<bool>("() => (" + OrdinaryElement + ")(document.activeElement)").WaitAsync(ct)) return new("forbidden");
                            await Action(session.Page.Keyboard.PressAsync(key));
                        }
                        break;
                    }
                case BrowserSelectOption args:
                    { var target = await Target(args.Target); if (target is null) return new("target_missing"); if (!await Ordinary(target)) return new("forbidden"); await Action(target.SelectOptionAsync(args.Values, new LocatorSelectOptionOptions { Timeout = TimeoutMs() })); break; }
                case BrowserClick args:
                    {
                        var target = await Target(args.Target); if (target is null) return new("target_missing"); if (!await Ordinary(target)) return new("forbidden");
                        var modifiers = args.Modifiers?.Select(v => Enum.Parse<KeyboardModifier>(v)).ToArray() ?? [];
                        await Action(target.ClickAsync(new LocatorClickOptions { Timeout = TimeoutMs(), ClickCount = args.ClickCount, Button = Enum.Parse<MouseButton>(args.Button, true), Modifiers = modifiers })); break;
                    }
                case BrowserTypeText args:
                    {
                        var target = await Target(args.Target); if (target is null) return new("target_missing");
                        if (!await Ordinary(target)) return new("forbidden");
                        await Action(target.FillAsync(args.Slowly ? "" : args.Text!, new LocatorFillOptions { Timeout = TimeoutMs() }));
                        if (args.Slowly)
                        {
                            target = await Target(args.Target); if (target is null) return new("target_missing");
                            if (!await Ordinary(target)) return new("forbidden");
                            await Action(target.PressSequentiallyAsync(args.Text!, new LocatorPressSequentiallyOptions { Timeout = TimeoutMs() }));
                        }
                        if (args.Submit)
                        {
                            target = await Target(args.Target); if (target is null) return new("target_missing");
                            if (!await Ordinary(target)) return new("forbidden");
                            await Action(target.PressAsync("Enter", new LocatorPressOptions { Timeout = TimeoutMs() }));
                        }
                        break;
                    }
                case BrowserDrop args:
                    {
                        var target = await Target(args.Target); if (target is null) return new("target_missing"); if (!await Ordinary(target)) return new("forbidden");
                        var text = args.Text; if (text is null) return new("invalid");
                        await Action(target.EvaluateAsync("(el, data) => { const transfer = new DataTransfer(); transfer.setData(data.mime, data.text); el.dispatchEvent(new DragEvent('drop', {bubbles:true,dataTransfer:transfer})); }", new { mime = args.MimeType ?? "text/plain", text })); break;
                    }
                case BrowserHighlight args:
                    {
                        var operation = args.Operation;
                        if (operation is not ("show" or "hide")) return new("invalid");
                        if (operation == "hide" && args.Target is null) await session.Page.HideHighlightAsync().WaitAsync(ct);
                        else
                        {
                            var target = await Target(args.Target); if (target is null) return new("target_missing");
                            await (operation == "hide" ? target.HideHighlightAsync() : target.HighlightAsync()).WaitAsync(ct);
                        }
                        return Data(new { status = "ok" });
                    }
                case BrowserGenerateLocator args:
                    {
                        var target = await Target(args.Target); if (target is null) return new("target_missing");
                        return Data(new { locator = Redact(target.ToString() ?? "", await CollectSecretsAsync(session, ct), session.ProtectedValues), informationalOnly = true });
                    }
                case BrowserVerify args:
                    {
                        var target = await Target(args.Target ?? new BrowserTarget("text", args.Text!, Exact: false), args.Condition == "hidden");
                        if (target is null) return new("target_missing");
                        if (args.Condition == "value" && !await Ordinary(target)) return new("forbidden");
                        var passed = args.Condition switch
                        { "visible" => await target.IsVisibleAsync().WaitAsync(ct), "hidden" => !await target.IsVisibleAsync().WaitAsync(ct), "checked" => await target.IsCheckedAsync().WaitAsync(ct), "text" => (await target.InnerTextAsync().WaitAsync(ct)).Contains(args.Text ?? "", StringComparison.Ordinal), "value" => await target.InputValueAsync().WaitAsync(ct) == args.Value, _ => false };
                        return Data(new { passed }) with { ApplicationOutcomeVerified = passed };
                    }
                case BrowserMouse args:
                    {
                        var x = args.X; var y = args.Y; var viewport = session.Page.ViewportSize;
                        if (viewport is null || !float.IsFinite(x) || !float.IsFinite(y) || x < 0 || y < 0 || x > viewport.Width || y > viewport.Height) return new("invalid");
                        async Task<bool> SafePoint(float px, float py) => await session.Page.EvaluateAsync<bool>(
                            "p => { const e = document.elementFromPoint(p.x,p.y); if (!e) return false; for (let n=e;n;n=n.parentElement) if (!(" + OrdinaryElement + ")(n)) return false; return true; }", new { x = (double)px, y = (double)py }).WaitAsync(ct);
                        if (!await SafePoint(x, y)) return new("target_denied");
                        switch (args.Operation)
                        {
                            case "move": await session.Page.Mouse.MoveAsync(x, y).WaitAsync(ct); break;
                            case "click": await Action(session.Page.Mouse.ClickAsync(x, y)); break;
                            case "down": await session.Page.Mouse.MoveAsync(x, y).WaitAsync(ct); await Action(session.Page.Mouse.DownAsync()); break;
                            case "up": await Action(session.Page.Mouse.UpAsync()); break;
                            case "wheel":
                                if (args.DeltaX is null && args.DeltaY is null) return new("invalid");
                                var deltaX = args.DeltaX ?? 0;
                                var deltaY = args.DeltaY ?? 0;
                                if (!float.IsFinite(deltaX) || !float.IsFinite(deltaY) || Math.Abs(deltaX) > 2000 || Math.Abs(deltaY) > 2000) return new("invalid");
                                await session.Page.Mouse.MoveAsync(x, y).WaitAsync(ct);
                                await Action(session.Page.Mouse.WheelAsync(deltaX, deltaY)); break;
                            case "drag":
                                var tx = (args.TargetX ?? -1); var ty = (args.TargetY ?? -1); if (tx > viewport.Width || ty > viewport.Height) return new("invalid");
                                if (!await SafePoint(tx, ty)) return new("target_denied");
                                await session.Page.Mouse.MoveAsync(x, y).WaitAsync(ct); await Action(session.Page.Mouse.DownAsync()); await session.Page.Mouse.MoveAsync(tx, ty).WaitAsync(ct); await Action(session.Page.Mouse.UpAsync()); break;
                            default: return new("invalid");
                        }
                        break;
                    }
                case BrowserEmulateMedia args:
                    await EmulateMediaAsync(session, args, ct, Action); break;
                case BrowserConsoleMessages args:
                    {
                        var secrets = await CollectSecretsAsync(session, ct); string[] messages;
                        var level = args.Level;
                        lock (session.PopupGate) messages = session.Console.Where(message => level == "all" || message.StartsWith(level + ":", StringComparison.Ordinal))
                            .TakeLast(args.Limit).Select(message => Redact(message, secrets, session.ProtectedValues)).ToArray();
                        return Data(new { messages, untrustedBrowserContent = true });
                    }
                case BrowserNetworkRequests args:
                    lock (session.PopupGate) return Data(new { requests = session.Network.TakeLast(args.Limit).Select(r => new { requestRef = r.Key, method = r.Value.Method, url = SafeNetworkUrl(r.Value.Url), resourceType = r.Value.ResourceType }), untrustedBrowserContent = true });
                case BrowserNetworkRequest args:
                    lock (session.PopupGate)
                    {
                        if (!session.Network.TryGetValue(args.RequestRef ?? "", out var request)) return new("invalid");
                        return Data(new { method = request.Method, url = SafeNetworkUrl(request.Url), resourceType = request.ResourceType });
                    }
                case BrowserRoute:
                case BrowserRoutes:
                case BrowserUnroute:
                case BrowserNetworkState:
                case BrowserCookies:
                case BrowserLocalStorage:
                case BrowserSessionStorage:
                    return await StateCommandAsync(session, command.Command, ct, Action);
                default: return new("unsupported_operation");
            }
            if (session.Dialog is not null) return new("dialog_pending");
            await AdoptOpenWebPageAsync(session, ct);
            await SettlePopupsAsync(session);
            if (session.PopupCode is not null) return new(session.PopupCode);
            if (session.DeniedNavigation || !IsAllowed(session, session.Page.Url))
            { await RestoreAllowedPageAsync(session, ct); return new("target_denied"); }
            var captured = await CaptureWithRetryAsync(session, command.SessionId, metadata.Feature.ToString(),
                metadata.Effect == ToolEffect.ReadOnly ? BrowserSnapshotSettle.None : (command.Operation is BrowserOperation.Type or BrowserOperation.Upload or BrowserOperation.Hover ? BrowserSnapshotSettle.None : BrowserSnapshotSettle.Automatic), null, ct);
            // Page/context events may arrive while native observation is captured.
            await SettlePopupsAsync(session);
            if (session.PopupCode is not null) return new(session.PopupCode);
            if (session.DeniedNavigation || !IsAllowed(session, session.Page.Url))
            { await RestoreAllowedPageAsync(session, ct); return new("target_denied"); }
            return captured;
        }

        async Task Action(Task action)
        {
            activeAction = action;
            effectAttempted = true; effectConfirmed = false;
            ActionStartedProbe?.Invoke();
            if (await Task.WhenAny(action, session.DialogSignal.Task).WaitAsync(ct) != action)
            {
                session.PendingAction = action;
                _ = action.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                throw new BrowserDialogPendingException();
            }
            await action.WaitAsync(ct);
            activeAction = null; effectConfirmed = true;
        }
        async Task FenceActionAsync()
        {
            if (activeAction is null || activeAction.IsCompleted) return;
            // Closing the page cancels a native pending locator action before another operation can acquire the gate.
            await FencePageAsync(session, command.SessionId);
            _ = activeAction.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
        async Task<ILocator?> Target(BrowserTarget? target, bool allowMissing = false) =>
            await ResolveTargetAsync(session, target, metadata.Effect != ToolEffect.ReadOnly, ct, allowMissing,
                requireAction: metadata.Feature == BrowserFeature.Click);
        async Task<bool> Ordinary(ILocator target) => await target.EvaluateAsync<bool>(OrdinaryElement).WaitAsync(ct);
    }

    private sealed class BrowserTargetException(string code) : Exception { public string Code { get; } = code; }

    private sealed class BrowserDialogPendingException : Exception;
    private sealed class BrowserTargetDeniedException : Exception;

    private static string SafeNetworkUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? $"{uri.Scheme}://{uri.IdnHost}:{uri.Port}" : "";
}
