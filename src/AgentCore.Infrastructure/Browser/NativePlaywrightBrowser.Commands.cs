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
        var args = command.Options;
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
        if (command.Operation == BrowserOperation.Screenshot)
        {
            var capture = await CaptureViewportAsync(new BrowserScreenshotRequest(command.SessionId, args.Format ?? "png",
                args.FullPage == true, args.TargetRef), cancellationToken);
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
        session.DialogSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        BrowserResult Data(object value) => new(null, DataJson: JsonSerializer.Serialize(value));
        try
        {
            if (command.Operation == BrowserOperation.GetConfig) return Configuration(session);
            if (session.Dialog is not null && command.Operation != BrowserOperation.Dialog) return new("dialog_pending");
            if (command.Operation is not (BrowserOperation.Tabs or BrowserOperation.Dialog)) await AdoptOpenWebPageAsync(session, ct);
            if (!IsAllowed(session, session.Page.Url)) return new("target_denied");
            var readsOnly = metadata.Effect == ToolEffect.ReadOnly || command.Operation == BrowserOperation.Tabs && args.Operation == "list"
                || command.Operation == BrowserOperation.Dialog && args.Operation == "inspect";
            if (!readsOnly && !BrowserTargetPolicy.EvaluateAct(_policy.InteractionMode,
                session.Page.Url, _policy.EffectiveInteractionOrigins, _policy.PolicyMode).Allowed) return new("forbidden");
            switch (command.Operation)
            {
                case BrowserOperation.SetGeolocation:
                    return await GeolocationAsync(session, args, ct);
                case BrowserOperation.Snapshot:
                    {
                        var scopeRef = command.Options.TargetRef;
                        var scope = scopeRef is null ? session.Page.Locator("body") : await Target(scopeRef);
                        if (scope is null) return new("stale_reference");
                        return new(null, await ObserveAsync(session, command.SessionId, scope,
                            command.Options.Depth ?? 32, scopeRef, ct, command.Options.Boxes == true));
                    }
                case BrowserOperation.Find:
                    return await FindAsync(session, command.SessionId, command.Options.Query, ct);
                case BrowserOperation.Upload:
                    {
                        var target = await Target(command.Options.Ref);
                        if (target is null) return new("stale_reference");
                        if (!await Ordinary(target)) return new("forbidden");
                        if (command.Options.Uploads is not { Count: > 0 and <= 8 } uploads) return new("invalid");
                        await Action(target.SetInputFilesAsync(uploads.Select(u => new FilePayload
                        { Name = u.FileName, MimeType = u.MediaType, Buffer = u.Content.ToArray() }), new() { Timeout = TimeoutMs() }));
                        break;
                    }
                case BrowserOperation.Hover:
                    {
                        var target = await Target(command.Options.Ref);
                        if (target is null) return new("stale_reference");
                        if (!await Ordinary(target)) return new("forbidden");
                        await Action(target.HoverAsync(new() { Timeout = TimeoutMs() })); break;
                    }
                case BrowserOperation.Drag:
                    {
                        var source = await Target(command.Options.Ref); var destination = await Target(command.Options.TargetRef);
                        if (source is null || destination is null) return new("stale_reference");
                        if (!await Ordinary(source) || !await Ordinary(destination)) return new("forbidden");
                        await Action(source.DragToAsync(destination, new() { Timeout = TimeoutMs() })); break;
                    }
                case BrowserOperation.Tabs:
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
                            var creation = session.Context.NewPageAsync();
                            await MutateContextAsync(session, creation, ct);
                            var page = await creation; RememberPage(session, page);
                            var navigation = page.GotoAsync(url!, new PageGotoOptions { Timeout = TimeoutMs(), WaitUntil = WaitUntilState.DOMContentLoaded });
                            try { await navigation.WaitAsync(ct); }
                            catch (Exception ex) when (ex is OperationCanceledException or PlaywrightException)
                            {
                                await CloseQuietlyAsync(page); ForgetPage(session, page);
                                _ = navigation.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                                throw;
                            }
                            if (!IsAllowed(session, page.Url)) { await page.CloseAsync().WaitAsync(ct); return new("target_denied"); }
                            session.Page = page; session.LastAllowedUrl = page.Url; session.Generation++;
                            return new(null, await CaptureAsync(session, command.SessionId, ct));
                        }
                        var binding = FindPage(session, args.TabRef);
                        if (binding is null || binding.Page.IsClosed) return new("stale_tab");
                        if (!IsAllowed(session, binding.Page.Url)) return new("target_denied");
                        if (operation == "close")
                        {
                            if (OpenPages(session).Count == 1) return new("last_tab");
                            ForgetPage(session, binding.Page); await binding.Page.CloseAsync().WaitAsync(ct);
                            if (ReferenceEquals(session.Page, binding.Page)) session.Page = OpenPages(session).First().Page;
                        }
                        else if (operation == "select")
                        {
                            // Bind cancellation fencing to the page being activated, not the previous tab.
                            session.Page = binding.Page; session.LastAllowedUrl = binding.Page.Url;
                            await Action(ActivateTabProbe is { } activate ? activate(binding.Page) : binding.Page.BringToFrontAsync());
                        }
                        else return new("invalid");
                        session.Generation++; return new(null, await CaptureAsync(session, command.SessionId, ct));
                    }
                case BrowserOperation.Dialog:
                    {
                        var dialog = session.Dialog;
                        if (dialog is null) return new("dialog_missing");
                        // A modal blocks page JS, including the live storage/password secret collector.
                        // Mask the entire untrusted message rather than trusting stale pre-dialog evidence.
                        if (args.Operation == "inspect") return Data(new { kind = dialog.Type, message = "[redacted]", messageRedacted = true });
                        if (args.Operation == "accept") await dialog.AcceptAsync(args.PromptText).WaitAsync(ct);
                        else await dialog.DismissAsync().WaitAsync(ct);
                        session.Dialog = null;
                        if (session.PendingAction is { } pending) { await pending.WaitAsync(ct); session.PendingAction = null; }
                        session.DialogSignal = new(TaskCreationOptions.RunContinuationsAsynchronously); return new(null, await CaptureAsync(session, command.SessionId, ct));
                    }
                case BrowserOperation.WaitFor:
                    {
                        var condition = args.Condition; var text = args.Text;
                        if (condition == "stable")
                        {
                            var settled = await BrowserPageSettle.WaitAsync(session.Page, (args.TimeoutMs ?? 2500), ct);
                            return new(null, (await CaptureAsync(session, command.SessionId, ct)) with { Settled = settled });
                        }
                        if (condition is "text" or "textGone")
                        {
                            if (text is null) return new("invalid");
                            // A hidden first match must neither block an existing visible match
                            // nor prove that every matching text has disappeared.
                            await session.Page.GetByText(text, new PageGetByTextOptions { Exact = false })
                                .Filter(new() { Visible = true }).First.WaitForAsync(new LocatorWaitForOptions
                            { State = condition == "text" ? WaitForSelectorState.Visible : WaitForSelectorState.Hidden, Timeout = (args.TimeoutMs ?? 2500) }).WaitAsync(ct);
                        }
                        else if (condition == "target")
                        {
                            var state = args.State ?? "visible";
                            var target = await Target(args.Ref, state is "hidden" or "detached"); if (target is null) return new("stale_reference");
                            if (state is "enabled" or "disabled")
                            {
                                await Assertions.Expect(target).ToBeEnabledAsync(new LocatorAssertionsToBeEnabledOptions { Enabled = state == "enabled", Timeout = (args.TimeoutMs ?? 2500) }).WaitAsync(ct);
                            }
                            else if (Enum.TryParse<WaitForSelectorState>(state, true, out var parsed))
                                await target.WaitForAsync(new LocatorWaitForOptions { State = parsed, Timeout = (args.TimeoutMs ?? 2500) }).WaitAsync(ct);
                            else return new("invalid");
                        }
                        else if (condition == "url")
                        {
                            var url = args.Url; if (url is null) return new("invalid");
                            await session.Page.WaitForURLAsync(url, new PageWaitForURLOptions { Timeout = (args.TimeoutMs ?? 2500) }).WaitAsync(ct);
                        }
                        else if (condition == "load") await session.Page.WaitForLoadStateAsync(args.State == "load" ? LoadState.Load : LoadState.DOMContentLoaded,
                            new PageWaitForLoadStateOptions { Timeout = (args.TimeoutMs ?? 2500) }).WaitAsync(ct);
                        else return new("invalid");
                        return new(null, await CaptureAsync(session, command.SessionId, ct));
                    }
                case BrowserOperation.Scroll:
                    if (command.Options.Ref is { } scrollRef)
                    {
                        var target = await Target(scrollRef);
                        if (target is null) return new("stale_reference");
                        if (!await Ordinary(target)) return new("forbidden");
                        await target.HoverAsync(new() { Timeout = TimeoutMs() }).WaitAsync(ct);
                    }
                    await Action(session.Page.Mouse.WheelAsync(command.Options.DeltaX ?? 0, command.Options.DeltaY ?? 0));
                    break;
                case BrowserOperation.Resize:
                    await Action(ResizeProbe is { } resizeProbe
                        ? resizeProbe(session.Page, (args.Width ?? 1280), (args.Height ?? 800))
                        : session.Page.SetViewportSizeAsync((args.Width ?? 1280), (args.Height ?? 800))); break;
                case BrowserOperation.FillForm:
                    {
                        var fields = new List<(string Ref, string? Value, bool? Checked)>();
                        foreach (var field in args.Fields ?? [])
                        {
                            if ((field.Value is not null) == (field.Checked is not null)) return new("invalid");
                            var target = await Target(field.Ref); if (target is null) return new("stale_reference");
                            if (!await Ordinary(target)) return new("forbidden");
                            fields.Add((field.Ref, field.Value, field.Checked));
                        }
                        foreach (var field in fields)
                        {
                            // Earlier input handlers may rerender, duplicate or protect a later field.
                            // Retain all-field preflight and recheck the live authority before each effect.
                            var target = await Target(field.Ref); if (target is null) return new("stale_reference");
                            if (!await Ordinary(target)) return new("forbidden");
                            if (field.Checked is bool check) await Action(target.SetCheckedAsync(check, new LocatorSetCheckedOptions { Timeout = TimeoutMs() }));
                            else if (field.Value is { } value) await Action(target.FillAsync(value, new LocatorFillOptions { Timeout = TimeoutMs() }));
                            else return new("invalid");
                        }
                        break;
                    }
                case BrowserOperation.PressKey:
                    {
                        var key = args.Key; if (key is null || !Regex.IsMatch(key, "^[A-Za-z0-9+_-]{1,80}$")) return new("invalid");
                        if (args.Ref is { } reference)
                        { var target = await Target(reference); if (target is null) return new("stale_reference"); if (!await Ordinary(target)) return new("forbidden"); await Action(target.PressAsync(key, new LocatorPressOptions { Timeout = TimeoutMs() })); }
                        else
                        {
                            if (!await session.Page.EvaluateAsync<bool>("() => (" + OrdinaryElement + ")(document.activeElement)").WaitAsync(ct)) return new("forbidden");
                            await Action(session.Page.Keyboard.PressAsync(key));
                        }
                        break;
                    }
                case BrowserOperation.SelectOption:
                    { var target = await Target(args.Ref); if (target is null) return new("stale_reference"); if (!await Ordinary(target)) return new("forbidden"); await Action(target.SelectOptionAsync(args.Values ?? [], new LocatorSelectOptionOptions { Timeout = TimeoutMs() })); break; }
                case BrowserOperation.Click:
                    {
                        var target = await Target(args.Ref); if (target is null) return new("stale_reference"); if (!await Ordinary(target)) return new("forbidden");
                        var modifiers = args.Modifiers?.Select(v => Enum.Parse<KeyboardModifier>(v)).ToArray() ?? [];
                        await Action(target.ClickAsync(new LocatorClickOptions { Timeout = TimeoutMs(), ClickCount = (args.ClickCount ?? 1), Button = Enum.Parse<MouseButton>(args.Button ?? "left", true), Modifiers = modifiers })); break;
                    }
                case BrowserOperation.Type:
                    {
                        var target = await Target(args.Ref); if (target is null) return new("stale_reference");
                        if (!await Ordinary(target)) return new("forbidden");
                        await Action(target.FillAsync(args.Slowly == true ? "" : args.Text!, new LocatorFillOptions { Timeout = TimeoutMs() }));
                        if (args.Slowly == true)
                        {
                            target = await Target(args.Ref); if (target is null) return new("stale_reference");
                            if (!await Ordinary(target)) return new("forbidden");
                            await Action(target.PressSequentiallyAsync(args.Text!, new LocatorPressSequentiallyOptions { Timeout = TimeoutMs() }));
                        }
                        if (args.Submit == true)
                        {
                            target = await Target(args.Ref); if (target is null) return new("stale_reference");
                            if (!await Ordinary(target)) return new("forbidden");
                            await Action(target.PressAsync("Enter", new LocatorPressOptions { Timeout = TimeoutMs() }));
                        }
                        break;
                    }
                case BrowserOperation.Drop:
                    {
                        var target = await Target(args.Ref); if (target is null) return new("stale_reference"); if (!await Ordinary(target)) return new("forbidden");
                        var text = args.Text; if (text is null) return new("invalid");
                        await Action(target.EvaluateAsync("(el, data) => { const transfer = new DataTransfer(); transfer.setData(data.mime, data.text); el.dispatchEvent(new DragEvent('drop', {bubbles:true,dataTransfer:transfer})); }", new { mime = args.MimeType ?? "text/plain", text })); break;
                    }
                case BrowserOperation.Highlight:
                    {
                        var operation = args.Operation ?? "show";
                        if (operation is not ("show" or "hide")) return new("invalid");
                        if (operation == "hide" && args.Ref is null) await session.Page.HideHighlightAsync().WaitAsync(ct);
                        else
                        {
                            var target = await Target(args.Ref); if (target is null) return new("stale_reference");
                            await (operation == "hide" ? target.HideHighlightAsync() : target.HighlightAsync()).WaitAsync(ct);
                        }
                        return Data(new { status = "ok" });
                    }
                case BrowserOperation.GenerateLocator:
                    {
                        var target = await Target(args.Ref); if (target is null) return new("stale_reference");
                        return Data(new { locator = Redact(target.ToString() ?? "", await CollectSecretsAsync(session, ct), session.ProtectedValues), informationalOnly = true });
                    }
                case BrowserOperation.Verify:
                    {
                        var target = args.Ref is { } reference ? await Target(reference) : session.Page.GetByText(args.Text ?? "", new PageGetByTextOptions { Exact = false }).First;
                        if (target is null) return new("stale_reference");
                        if (args.Condition == "value" && !await Ordinary(target)) return new("forbidden");
                        var passed = args.Condition switch
                        { "visible" => await target.IsVisibleAsync().WaitAsync(ct), "hidden" => !await target.IsVisibleAsync().WaitAsync(ct), "checked" => await target.IsCheckedAsync().WaitAsync(ct), "text" => (await target.InnerTextAsync().WaitAsync(ct)).Contains(args.Text ?? "", StringComparison.Ordinal), "value" => await target.InputValueAsync().WaitAsync(ct) == args.Value, _ => false };
                        return Data(new { passed });
                    }
                case BrowserOperation.Mouse:
                    {
                        var x = (args.X ?? -1); var y = (args.Y ?? -1); var viewport = session.Page.ViewportSize;
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
                case BrowserOperation.EmulateMedia:
                    await EmulateMediaAsync(session, args, ct, Action); break;
                case BrowserOperation.ConsoleMessages:
                    {
                        var secrets = await CollectSecretsAsync(session, ct); string[] messages;
                        var level = args.Level ?? "all";
                        lock (session.PopupGate) messages = session.Console.Where(message => level == "all" || message.StartsWith(level + ":", StringComparison.Ordinal))
                            .TakeLast((args.Limit ?? 20)).Select(message => Redact(message, secrets, session.ProtectedValues)).ToArray();
                        return Data(new { messages, untrustedBrowserContent = true });
                    }
                case BrowserOperation.NetworkRequests:
                    lock (session.PopupGate) return Data(new { requests = session.Network.TakeLast((args.Limit ?? 20)).Select(r => new { requestRef = r.Key, method = r.Value.Method, url = SafeNetworkUrl(r.Value.Url), resourceType = r.Value.ResourceType }), untrustedBrowserContent = true });
                case BrowserOperation.NetworkRequest:
                    lock (session.PopupGate)
                    {
                        if (!session.Network.TryGetValue(args.RequestRef ?? "", out var request)) return new("invalid");
                        return Data(new { method = request.Method, url = SafeNetworkUrl(request.Url), resourceType = request.ResourceType });
                    }
                case BrowserOperation.Route:
                case BrowserOperation.Routes:
                case BrowserOperation.Unroute:
                case BrowserOperation.NetworkState:
                case BrowserOperation.Cookies:
                case BrowserOperation.LocalStorage:
                case BrowserOperation.SessionStorage:
                    return await StateCommandAsync(session, command, ct, Action);
                default: return new("unsupported_operation");
            }
            if (session.Dialog is not null) return new("dialog_pending");
            await AdoptOpenWebPageAsync(session, ct);
            await SettlePopupsAsync(session);
            if (session.PopupCode is not null) return new(session.PopupCode);
            if (session.DeniedNavigation || !IsAllowed(session, session.Page.Url))
            { await RestoreAllowedPageAsync(session, ct); return new("target_denied"); }
            return await CaptureWithRetryAsync(session, command.SessionId, metadata.Feature.ToString(),
                metadata.Effect == ToolEffect.ReadOnly ? BrowserSnapshotSettle.None : (command.Operation is BrowserOperation.Type or BrowserOperation.Upload or BrowserOperation.Hover ? BrowserSnapshotSettle.None : BrowserSnapshotSettle.Automatic), null, ct);
        }
        catch (BrowserDialogPendingException) { return new("dialog_pending"); }
        catch (BrowserReferenceException ex) { return new(ex.Code); }
        catch (BrowserTargetDeniedException) { return new("target_denied"); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await FenceActionAsync();
            RemoveRefs(command.SessionId);
            throw new OperationCanceledException(cancellationToken);
        }
        catch (OperationCanceledException) { await FenceActionAsync(); RemoveRefs(command.SessionId); return new("timeout"); }
        catch (RegexMatchTimeoutException) { return new("timeout"); }
        catch (TimeoutException) { return new("timeout"); }
        catch (PlaywrightException ex) { return new((await FailAsync(session, metadata.Feature.ToString(), "interaction", ex)).ErrorCode); }
        finally { session.Gate.Release(); }

        async Task Action(Task action)
        {
            activeAction = action;
            ActionStartedProbe?.Invoke();
            if (await Task.WhenAny(action, session.DialogSignal.Task).WaitAsync(ct) != action)
            {
                session.PendingAction = action;
                _ = action.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                throw new BrowserDialogPendingException();
            }
            await action.WaitAsync(ct);
            activeAction = null;
        }
        async Task FenceActionAsync()
        {
            if (activeAction is null || activeAction.IsCompleted) return;
            // Closing the page cancels a native pending locator action before another operation can acquire the gate.
            await FencePageAsync(session, command.SessionId);
            _ = activeAction.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
        async Task<ILocator?> Target(string? reference, bool allowMissing = false)
        {
            var error = ReferenceError(reference, command.SessionId, out var live);
            if (error is not null) throw new BrowserReferenceException(error);
            var count = await live!.Handle.CountAsync().WaitAsync(ct);
            if (count == 0 && !allowMissing) throw new BrowserReferenceException("target_missing");
            if (count == 0) return live.Handle;
            if (count > 1) throw new BrowserReferenceException("ambiguous_target");
            if (metadata.Feature == BrowserFeature.Click && live.Actions.Count == 0)
                throw new BrowserReferenceException("non_actionable_target");
            var frameUrl = await live.Handle.EvaluateAsync<string>("el => el.ownerDocument.location.href").WaitAsync(ct);
            if (metadata.Effect == ToolEffect.ReadOnly ? !Allows(session, frameUrl, true)
                : !BrowserTargetPolicy.EvaluateAct(_policy.InteractionMode, frameUrl, LeaseOrigins(session) ?? _policy.EffectiveInteractionOrigins, _policy.PolicyMode).Allowed) throw new BrowserTargetDeniedException();
            return live.Handle;
        }
        async Task<bool> Ordinary(ILocator target) => await target.EvaluateAsync<bool>(OrdinaryElement).WaitAsync(ct);
    }

    private sealed class BrowserReferenceException(string code) : Exception { public string Code { get; } = code; }

    private sealed class BrowserDialogPendingException : Exception;
    private sealed class BrowserTargetDeniedException : Exception;

    private static string SafeNetworkUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? $"{uri.Scheme}://{uri.IdnHost}:{uri.Port}" : "";
}
