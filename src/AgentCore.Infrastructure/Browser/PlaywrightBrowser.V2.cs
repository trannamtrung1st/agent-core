using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using Microsoft.Playwright;

namespace AgentCore.Infrastructure.Browser;

public sealed partial class PlaywrightBrowser
{
    // Evaluation, raw storage export, trace and video cannot guarantee secret masking in this host.
    public BrowserProviderDescriptor Provider { get; } = new("playwright", "Playwright", new HashSet<BrowserFeature>
    {
        BrowserFeature.Navigate, BrowserFeature.Snapshot, BrowserFeature.Find, BrowserFeature.Click,
        BrowserFeature.Hover, BrowserFeature.Drag, BrowserFeature.Drop, BrowserFeature.Type, BrowserFeature.FillForm,
        BrowserFeature.SelectOption, BrowserFeature.PressKey, BrowserFeature.Upload, BrowserFeature.FillCredential,
        BrowserFeature.Wait, BrowserFeature.Tabs, BrowserFeature.Dialog, BrowserFeature.Close, BrowserFeature.Screenshot,
        BrowserFeature.NetworkControl, BrowserFeature.Storage, BrowserFeature.Resize, BrowserFeature.Console, BrowserFeature.NetworkInspect, BrowserFeature.Testing,
        BrowserFeature.VisionMouse, BrowserFeature.Highlight, BrowserFeature.Media, BrowserFeature.Configuration, BrowserFeature.Geolocation
    }) { Engine = "chromium" };

    private static string? FindPageId(SessionBrowser session) => OpenPages(session).FirstOrDefault(x => ReferenceEquals(x.Page, session.Page))?.Id;
    private static string? String(JsonElement args, string key) => args.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    private static int Int(JsonElement args, string key, int fallback) => args.TryGetProperty(key, out var item) && item.TryGetInt32(out var number) ? number : fallback;

    public async ValueTask<BrowserCommandResult> ExecuteAsync(BrowserCommand command, CancellationToken cancellationToken = default)
    {
        if (!BrowserToolCatalog.TryGet(command.Tool, out var metadata) || !Provider.Supports(metadata.Feature)) return new("unsupported_operation");
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAvailable) return command.Tool == ToolCatalog.BrowserConfiguration ? Configuration(null) : new("provider_unavailable");
        if (!_sessions.TryGetValue(command.SessionId, out var session))
            return command.Tool == ToolCatalog.BrowserConfiguration ? Configuration(null) : new("provider_unavailable");
        if (command.Tool == "browser.screenshot")
        {
            var capture = await CaptureViewportAsync(new BrowserScreenshotRequest(command.SessionId, String(command.Arguments, "format") ?? "png",
                command.Arguments.TryGetProperty("fullPage", out var full) && full.GetBoolean(), String(command.Arguments, "targetRef")), cancellationToken);
            return new(capture.ErrorCode, Bytes: capture.Png, ContentType: capture.ContentType, FileName: "screenshot." + capture.ContentType.Split('/')[1]);
        }
        await using var interactive = await EnterInteractiveAsync(command.SessionId, cancellationToken).ConfigureAwait(false);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        // One deadline spans validation, provider reads, waits, action and final capture.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(command.Tool == "browser.wait_for" ? Int(command.Arguments, "timeoutMs", 2500) : TimeoutMs()));
        var ct = deadline.Token;
        var args = command.Arguments;
        Task? activeAction = null;
        session.DialogSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        BrowserCommandResult Data(object value) => new(null, DataJson: JsonSerializer.Serialize(value));
        try
        {
            if (command.Tool == ToolCatalog.BrowserConfiguration) return Configuration(session);
            if (session.Dialog is not null && command.Tool != "browser.dialog") return new("dialog_pending");
            if (!IsAllowed(session, session.Page.Url)) return new("target_denied");
            var readsOnly = metadata.Effect == ToolEffect.ReadOnly || command.Tool == "browser.tabs" && String(args, "operation") == "list"
                || command.Tool == "browser.dialog" && String(args, "operation") == "inspect";
            if (!readsOnly && !BrowserTargetPolicy.EvaluateAct(_policy.InteractionMode,
                session.Page.Url, _policy.EffectiveInteractionOrigins, _policy.PolicyMode).Allowed) return new("forbidden");
            switch (command.Tool)
            {
                case "browser.set_geolocation":
                    return await GeolocationAsync(session, args, ct);
                case "browser.snapshot":
                    {
                        ILocator? scope = null;
                        if (String(args, "targetRef") is { } targetRef)
                        {
                            scope = await Target(targetRef);
                            if (scope is null) return new("stale_reference");
                        }
                        var snapshot = await CaptureAsync(session, command.SessionId, ct).ConfigureAwait(false);
                        if (scope is not null) snapshot = await ScopeSnapshotAsync(session, command.SessionId, snapshot, scope, ct);
                        var depth = Int(args, "depth", 32);
                        var content = string.Join('\n', snapshot.Content!.Split('\n').Where(line => line.TakeWhile(char.IsWhiteSpace).Count() / 2 < depth));
                        var boxes = new List<BrowserTargetBox>();
                        if (args.TryGetProperty("boxes", out var showBoxes) && showBoxes.GetBoolean())
                            foreach (var element in snapshot.Elements.Take(20))
                                if (_refs.TryGetValue(element.Ref, out var live) && await live.Handle.BoundingBoxAsync().WaitAsync(ct) is { } box)
                                    boxes.Add(new(element.Ref, box.X, box.Y, box.Width, box.Height));
                        return new(null, snapshot with { Content = content, Boxes = boxes });
                    }
                case "browser.find":
                    {
                        var text = String(args, "text"); var pattern = String(args, "regex");
                        if ((text is null) == (pattern is null)) return new("invalid");
                        if (session.SnapshotId.Length == 0) return new("stale_reference");
                        Regex? regex = null;
                        if (pattern is not null)
                        {
                            try { regex = new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50)); }
                            catch (ArgumentException) { return new("invalid"); }
                        }
                        var matches = session.SnapshotIndex.Where(e => regex?.IsMatch(e.Name + " " + e.Role) ?? (e.Name + " " + e.Role).Contains(text!, StringComparison.OrdinalIgnoreCase))
                            .Take(Int(args, "limit", 10)).Select(e => new { @ref = e.Ref, role = e.Role, name = e.Name, actions = e.Actions, state = e.State }).ToArray();
                        return Data(new { snapshotId = session.SnapshotId, tabRef = FindPageId(session), matches, untrustedBrowserContent = true });
                    }
                case "browser.tabs":
                    {
                        RememberOpenPages(session);
                        var operation = String(args, "operation");
                        if (operation is not ("list" or "new" or "select" or "close")) return new("invalid");
                        if (operation == "list") return Data(new { tabs = DescribePages(session).Select(t => new { tabRef = t.PageId, url = t.Url, active = t.Active }) });
                        if (operation == "new")
                        {
                            var url = String(args, "url");
                            if (!BrowserTargetPolicy.EvaluateDestination(url, LeaseOrigins(session) ?? _policy.NavigationOrigins, _policy.PolicyMode).Allowed) return new("target_denied");
                            var page = await session.Context.NewPageAsync().WaitAsync(ct); RememberPage(session, page);
                            await page.GotoAsync(url!, new PageGotoOptions { Timeout = TimeoutMs(), WaitUntil = WaitUntilState.DOMContentLoaded }).WaitAsync(ct);
                            if (!IsAllowed(session, page.Url)) { await page.CloseAsync().WaitAsync(ct); return new("target_denied"); }
                            session.Page = page; session.LastAllowedUrl = page.Url; session.Generation++;
                            return new(null, await CaptureAsync(session, command.SessionId, ct));
                        }
                        var binding = FindPage(session, String(args, "tabRef"));
                        if (binding is null || binding.Page.IsClosed) return new("stale_tab");
                        if (!IsAllowed(session, binding.Page.Url)) return new("target_denied");
                        if (operation == "close")
                        {
                            if (OpenPages(session).Count == 1) return new("last_tab");
                            ForgetPage(session, binding.Page); await binding.Page.CloseAsync().WaitAsync(ct);
                            if (ReferenceEquals(session.Page, binding.Page)) session.Page = OpenPages(session).First().Page;
                        }
                        else if (operation == "select") session.Page = binding.Page;
                        else return new("invalid");
                        session.Generation++; return new(null, await CaptureAsync(session, command.SessionId, ct));
                    }
                case "browser.dialog":
                    {
                        var dialog = session.Dialog;
                        if (dialog is null) return new("dialog_missing");
                        if (String(args, "operation") == "inspect") return Data(new { kind = dialog.Type, message = Redact(dialog.Message, [], session.ProtectedValues) });
                        if (String(args, "operation") == "accept") await dialog.AcceptAsync(String(args, "promptText")).WaitAsync(ct);
                        else await dialog.DismissAsync().WaitAsync(ct);
                        session.Dialog = null;
                        if (session.PendingAction is { } pending) { await pending.WaitAsync(ct); session.PendingAction = null; }
                        session.DialogSignal = new(TaskCreationOptions.RunContinuationsAsynchronously); return new(null, await CaptureAsync(session, command.SessionId, ct));
                    }
                case "browser.wait_for":
                    {
                        var condition = String(args, "condition"); var text = String(args, "text");
                        if (condition == "stable")
                        {
                            var settled = await BrowserPageSettle.WaitAsync(session.Page, Int(args, "timeoutMs", 2500), ct);
                            return new(null, (await CaptureAsync(session, command.SessionId, ct)) with { Settled = settled });
                        }
                        if (condition is "text" or "textGone")
                        {
                            if (text is null) return new("invalid");
                            await session.Page.GetByText(text, new PageGetByTextOptions { Exact = false }).First.WaitForAsync(new LocatorWaitForOptions
                            { State = condition == "text" ? WaitForSelectorState.Visible : WaitForSelectorState.Hidden, Timeout = Int(args, "timeoutMs", 2500) }).WaitAsync(ct);
                        }
                        else if (condition == "target")
                        {
                            var target = await Target(String(args, "ref")); if (target is null) return new("stale_reference");
                            var state = String(args, "state") ?? "visible";
                            if (state is "enabled" or "disabled")
                            {
                                await Assertions.Expect(target).ToBeEnabledAsync(new LocatorAssertionsToBeEnabledOptions { Enabled = state == "enabled", Timeout = Int(args, "timeoutMs", 2500) }).WaitAsync(ct);
                            }
                            else if (Enum.TryParse<WaitForSelectorState>(state, true, out var parsed))
                                await target.WaitForAsync(new LocatorWaitForOptions { State = parsed, Timeout = Int(args, "timeoutMs", 2500) }).WaitAsync(ct);
                            else return new("invalid");
                        }
                        else if (condition == "url")
                        {
                            var url = String(args, "url"); if (url is null) return new("invalid");
                            await session.Page.WaitForURLAsync(url, new PageWaitForURLOptions { Timeout = Int(args, "timeoutMs", 2500) }).WaitAsync(ct);
                        }
                        else if (condition == "load") await session.Page.WaitForLoadStateAsync(String(args, "state") == "load" ? LoadState.Load : LoadState.DOMContentLoaded,
                            new PageWaitForLoadStateOptions { Timeout = Int(args, "timeoutMs", 2500) }).WaitAsync(ct);
                        else return new("invalid");
                        return new(null, await CaptureAsync(session, command.SessionId, ct));
                    }
                case "browser.resize":
                    await session.Page.SetViewportSizeAsync(Int(args, "width", 1280), Int(args, "height", 800)).WaitAsync(ct); break;
                case "browser.fill_form":
                    {
                        var fields = new List<(ILocator Target, string? Value, bool? Checked)>();
                        foreach (var field in args.GetProperty("fields").EnumerateArray())
                        {
                            if (field.TryGetProperty("value", out _) == field.TryGetProperty("checked", out _)) return new("invalid");
                            var target = await Target(String(field, "ref")); if (target is null) return new("stale_reference");
                            if (!await Ordinary(target)) return new("unsupported_operation");
                            fields.Add((target, String(field, "value"), field.TryGetProperty("checked", out var check) ? check.GetBoolean() : null));
                        }
                        foreach (var field in fields)
                        {
                            if (field.Checked is bool check) await Action(field.Target.SetCheckedAsync(check, new LocatorSetCheckedOptions { Timeout = TimeoutMs() }));
                            else if (field.Value is { } value) await Action(field.Target.FillAsync(value, new LocatorFillOptions { Timeout = TimeoutMs() }));
                            else return new("invalid");
                        }
                        break;
                    }
                case "browser.press_key":
                    {
                        var key = String(args, "key"); if (key is null || !Regex.IsMatch(key, "^[A-Za-z0-9+_-]{1,80}$")) return new("invalid");
                        if (String(args, "ref") is { } reference)
                        { var target = await Target(reference); if (target is null) return new("stale_reference"); if (!await Ordinary(target)) return new("unsupported_operation"); await Action(target.PressAsync(key, new LocatorPressOptions { Timeout = TimeoutMs() })); }
                        else
                        {
                            if (!await session.Page.EvaluateAsync<bool>("() => (" + OrdinaryElement + ")(document.activeElement)").WaitAsync(ct)) return new("forbidden");
                            await Action(session.Page.Keyboard.PressAsync(key));
                        }
                        break;
                    }
                case "browser.select_option":
                    { var target = await Target(String(args, "ref")); if (target is null) return new("stale_reference"); if (!await Ordinary(target)) return new("unsupported_operation"); await Action(target.SelectOptionAsync(args.GetProperty("values").EnumerateArray().Select(v => v.GetString()!), new LocatorSelectOptionOptions { Timeout = TimeoutMs() })); break; }
                case "browser.click":
                    {
                        var target = await Target(String(args, "ref")); if (target is null) return new("stale_reference"); if (!await Ordinary(target)) return new("unsupported_operation");
                        var modifiers = args.TryGetProperty("modifiers", out var m) ? m.EnumerateArray().Select(v => Enum.Parse<KeyboardModifier>(v.GetString()!)).ToArray() : [];
                        await Action(target.ClickAsync(new LocatorClickOptions { Timeout = TimeoutMs(), ClickCount = Int(args, "clickCount", 1), Button = Enum.Parse<MouseButton>(String(args, "button") ?? "left", true), Modifiers = modifiers })); break;
                    }
                case "browser.type":
                    {
                        var target = await Target(String(args, "ref")); if (target is null) return new("stale_reference");
                        if (!await Ordinary(target)) return new("unsupported_operation");
                        await Action(target.FillAsync(args.TryGetProperty("slowly", out var slowly) && slowly.GetBoolean() ? "" : String(args, "text")!, new LocatorFillOptions { Timeout = TimeoutMs() }));
                        if (slowly.ValueKind == JsonValueKind.True)
                            await Action(target.PressSequentiallyAsync(String(args, "text")!, new LocatorPressSequentiallyOptions { Timeout = TimeoutMs() }));
                        if (args.TryGetProperty("submit", out var submit) && submit.GetBoolean()) await Action(target.PressAsync("Enter", new LocatorPressOptions { Timeout = TimeoutMs() }));
                        break;
                    }
                case "browser.drop":
                    {
                        if (args.TryGetProperty("artifactId", out _)) return new("unsupported_operation");
                        var target = await Target(String(args, "ref")); if (target is null) return new("stale_reference"); if (!await Ordinary(target)) return new("unsupported_operation");
                        var text = String(args, "text"); if (text is null) return new("invalid");
                        await Action(target.EvaluateAsync("(el, data) => { const transfer = new DataTransfer(); transfer.setData(data.mime, data.text); el.dispatchEvent(new DragEvent('drop', {bubbles:true,dataTransfer:transfer})); }", new { mime = String(args, "mimeType") ?? "text/plain", text })); break;
                    }
                case "browser.highlight":
                    {
                        var operation = String(args, "operation") ?? "show";
                        if (operation is not ("show" or "hide")) return new("invalid");
                        if (operation == "hide" && String(args, "ref") is null) await session.Page.HideHighlightAsync().WaitAsync(ct);
                        else
                        {
                            var target = await Target(String(args, "ref")); if (target is null) return new("stale_reference");
                            await (operation == "hide" ? target.HideHighlightAsync() : target.HighlightAsync()).WaitAsync(ct);
                        }
                        return Data(new { status = "ok" });
                    }
                case "browser.generate_locator":
                    {
                        var target = await Target(String(args, "ref")); if (target is null) return new("stale_reference");
                        return Data(new { locator = Redact(target.ToString() ?? "", await CollectSecretsAsync(session, ct), session.ProtectedValues), informationalOnly = true });
                    }
                case "browser.verify":
                    {
                        var target = String(args, "ref") is { } reference ? await Target(reference) : session.Page.GetByText(String(args, "text") ?? "", new PageGetByTextOptions { Exact = false }).First;
                        if (target is null) return new("stale_reference");
                        if (String(args, "condition") == "value" && !await Ordinary(target)) return new("forbidden");
                        var passed = String(args, "condition") switch
                        { "visible" => await target.IsVisibleAsync().WaitAsync(ct), "hidden" => !await target.IsVisibleAsync().WaitAsync(ct), "checked" => await target.IsCheckedAsync().WaitAsync(ct), "text" => (await target.InnerTextAsync().WaitAsync(ct)).Contains(String(args, "text") ?? "", StringComparison.Ordinal), "value" => await target.InputValueAsync().WaitAsync(ct) == String(args, "value"), _ => false };
                        return Data(new { passed });
                    }
                case "browser.mouse":
                    {
                        var x = args.GetProperty("x").GetSingle(); var y = args.GetProperty("y").GetSingle(); var viewport = session.Page.ViewportSize;
                        if (viewport is null || x > viewport.Width || y > viewport.Height) return new("invalid");
                        async Task<bool> SafePoint(float px, float py) => await session.Page.EvaluateAsync<bool>(
                            "p => { const e = document.elementFromPoint(p.x,p.y); return !!e && (" + OrdinaryElement + ")(e.closest('iframe,input,textarea,select,[contenteditable],label,button,a,[role]') || e); }", new { x = (double)px, y = (double)py }).WaitAsync(ct);
                        if (!await SafePoint(x, y)) return new("target_denied");
                        switch (String(args, "operation"))
                        {
                            case "move": await session.Page.Mouse.MoveAsync(x, y).WaitAsync(ct); break;
                            case "click": await Action(session.Page.Mouse.ClickAsync(x, y)); break;
                            case "down": await session.Page.Mouse.MoveAsync(x, y).WaitAsync(ct); await Action(session.Page.Mouse.DownAsync()); break;
                            case "up": await Action(session.Page.Mouse.UpAsync()); break;
                            case "wheel":
                                if (!args.TryGetProperty("deltaX", out var dx) && !args.TryGetProperty("deltaY", out _)) return new("invalid");
                                var deltaX = dx.ValueKind == JsonValueKind.Number ? dx.GetSingle() : 0;
                                var deltaY = args.TryGetProperty("deltaY", out var dy) ? dy.GetSingle() : 0;
                                if (!float.IsFinite(deltaX) || !float.IsFinite(deltaY) || Math.Abs(deltaX) > 2000 || Math.Abs(deltaY) > 2000) return new("invalid");
                                await session.Page.Mouse.MoveAsync(x, y).WaitAsync(ct);
                                await Action(session.Page.Mouse.WheelAsync(deltaX, deltaY)); break;
                            case "drag":
                                var tx = args.GetProperty("targetX").GetSingle(); var ty = args.GetProperty("targetY").GetSingle(); if (tx > viewport.Width || ty > viewport.Height) return new("invalid");
                                if (!await SafePoint(tx, ty)) return new("target_denied");
                                await session.Page.Mouse.MoveAsync(x, y).WaitAsync(ct); await Action(session.Page.Mouse.DownAsync()); await session.Page.Mouse.MoveAsync(tx, ty).WaitAsync(ct); await Action(session.Page.Mouse.UpAsync()); break;
                            default: return new("invalid");
                        }
                        break;
                    }
                case "browser.emulate_media":
                    await EmulateMediaAsync(session, args, ct, Action); break;
                case "browser.console_messages":
                    {
                        var secrets = await CollectSecretsAsync(session, ct); string[] messages;
                        var level = String(args, "level") ?? "all";
                        lock (session.PopupGate) messages = session.Console.Where(message => level == "all" || message.StartsWith(level + ":", StringComparison.Ordinal))
                            .TakeLast(Int(args, "limit", 20)).Select(message => Redact(message, secrets, session.ProtectedValues)).ToArray();
                        return Data(new { messages, untrustedBrowserContent = true });
                    }
                case "browser.network_requests":
                    lock (session.PopupGate) return Data(new { requests = session.Network.TakeLast(Int(args, "limit", 20)).Select(r => new { requestRef = r.Key, method = r.Value.Method, url = SafeNetworkUrl(r.Value.Url), resourceType = r.Value.ResourceType }), untrustedBrowserContent = true });
                case "browser.network_request":
                    lock (session.PopupGate)
                    {
                        if (!session.Network.TryGetValue(String(args, "requestRef") ?? "", out var request)) return new("invalid");
                        return Data(new { method = request.Method, url = SafeNetworkUrl(request.Url), resourceType = request.ResourceType });
                    }
                case "browser.route":
                case "browser.routes":
                case "browser.unroute":
                case "browser.network_state":
                case "browser.cookies":
                case "browser.local_storage":
                case "browser.session_storage":
                    return await StateCommandAsync(session, command, ct, Action);
                default: return new("unsupported_operation");
            }
            if (session.Dialog is not null) return new("dialog_pending");
            return new(null, await CaptureAsync(session, command.SessionId, ct), Downloads: await DrainDownloadsAsync(session, ct));
        }
        catch (BrowserDialogPendingException) { return new("dialog_pending"); }
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
        async Task<ILocator?> Target(string? reference)
        {
            if (reference is null || !_refs.TryGetValue(reference, out var live) || live.SessionId != command.SessionId || live.Generation != session.Generation) return null;
            var frameUrl = await live.Handle.EvaluateAsync<string>("el => el.ownerDocument.location.href").WaitAsync(ct);
            if (metadata.Effect == ToolEffect.ReadOnly ? !Allows(session, frameUrl, true)
                : !BrowserTargetPolicy.EvaluateAct(_policy.InteractionMode, frameUrl, LeaseOrigins(session) ?? _policy.EffectiveInteractionOrigins, _policy.PolicyMode).Allowed) throw new BrowserTargetDeniedException();
            return live.Handle;
        }
        async Task<bool> Ordinary(ILocator target) => await target.EvaluateAsync<bool>(OrdinaryElement).WaitAsync(ct);
    }

    private sealed class BrowserDialogPendingException : Exception;
    private sealed class BrowserTargetDeniedException : Exception;

    private static string SafeNetworkUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? $"{uri.Scheme}://{uri.IdnHost}:{uri.Port}" : "";
}
