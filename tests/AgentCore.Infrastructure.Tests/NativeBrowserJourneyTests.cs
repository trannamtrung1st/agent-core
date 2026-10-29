using AgentCore.Tests.Shared;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Browser;
namespace AgentCore.Infrastructure.Tests;

[Collection(BrowserChromiumCollection.Name)]
public sealed class NativeBrowserJourneyTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public async Task Readable_frame_ref_cannot_bypass_the_interaction_origin_policy()
    {
        await using var main = new LoopbackBrowserFixtureHost(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        await using var readOnly = new LoopbackBrowserFixtureHost(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        await main.StartAsync(0, CancellationToken.None); await readOnly.StartAsync(0, CancellationToken.None);
        var browser = new NativePlaywrightBrowser(new BrowserOptions { Enabled = true, Headless = true, FixtureEnabled = false,
            InteractionMode = "InteractiveDemo", NavigationOrigins = [main.Origin!, readOnly.Origin!], InteractionOrigins = [main.Origin!] }, loggerFactory: null);
        await browser.StartAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        try
        {
            Assert.All(AgentCore.Application.Tools.BrowserToolCatalog.Tools.Values.Where(t => t.Group == "core"), t => Assert.True(browser.Provider.Supports(t.Feature), t.Name));
            Assert.Null((await browser.ExecuteAsync(BrowserTestRequests.Navigate(id, new Uri(main.Origin! + "/browser-native.html?compact=1")))).ErrorCode);
            var page = browser.ContextFor(id)!.Pages[0];
            await NativeBrowserFixtureActions.ApplyAsync(browser, id, page.MainFrame,
                "url => { document.querySelector('iframe').src = url; }", readOnly.Origin + "/browser-native-frame.html");
            await page.FrameLocator("iframe").GetByRole(Microsoft.Playwright.AriaRole.Button, new() { Name = "Save frame" }).WaitForAsync();
            var snapshot = await browser.ExecuteAsync(BrowserTestRequests.Inspect(id)); Assert.Null(snapshot.ErrorCode);
            var observation = (await browser.ExecuteAsync(BrowserTestRequests.Inspect(id))).Observation!;
            var frameRef = Assert.Single(observation.Frames!).Ref;
            var discovered = await browser.ExecuteAsync(new(id, new BrowserFind(Target: new BrowserTarget("role", "button", Name: "Save frame", FrameRef: frameRef))));
            Assert.Null(discovered.ErrorCode);
            var reference = JsonDocument.Parse(discovered.DataJson!).RootElement.GetProperty("matches")[0].GetProperty("target").Deserialize<BrowserTarget>(JsonSerializerOptions.Web)!;
            Assert.Equal("target_denied", (await browser.ExecuteAsync(BrowserTestRequests.Interaction(id, BrowserOperation.Click, reference, null))).ErrorCode);
            Assert.Equal("target_denied", (await browser.ExecuteAsync(AgentCore.Application.Tools.BrowserToolArguments.Request(id, "browser.click", JsonSerializer.SerializeToElement(new { target = reference, clickCount = 1 })))).ErrorCode);
            Assert.Equal("Save frame", await page.FrameLocator("iframe").GetByRole(Microsoft.Playwright.AriaRole.Button).InnerTextAsync());
        }
        finally { await browser.StopAsync(CancellationToken.None); }
    }
    [Theory]
    [InlineData(720, false)]
    [InlineData(400, false)]
    [InlineData(720, true)]
    public async Task Native_forms_keyboard_visual_target_and_pending_action_cancellation_recover(int viewportHeight, bool withholdFirstCapture)
    {
        var releaseRecovery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var browser = new NativePlaywrightBrowser(new BrowserOptions { Enabled = true, Headless = true, FixtureEnabled = true,
            FixturePort = 0, Environment = new() { ViewportWidth = 1280, ViewportHeight = viewportHeight }, InteractionMode = "InteractiveDemo", NavigationOrigins = ["http://127.0.0.1:5091"], InteractionOrigins = ["http://127.0.0.1:5091"] }, loggerFactory: null);
        await browser.StartAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        try
        {
            Assert.Null((await browser.ExecuteAsync(BrowserTestRequests.Navigate(id, new Uri(browser.HostPolicy.NavigationOrigins.Single() + "/browser-native.html?compact=1")))).ErrorCode);
            async Task<BrowserResult> Run(string tool, object args) => await browser.ExecuteAsync(AgentCore.Application.Tools.BrowserToolArguments.Request(id, tool, JsonSerializer.SerializeToElement(args)));
            async Task<BrowserTarget> Find(string name) => (await BrowserTestQueries.Find(browser, id, name)).Target;
            Assert.Null((await Run("browser.fill_form", new { fields = new[] { new { target = await Find("Enabled"), @checked = true } } })).ErrorCode);
            Assert.True(await browser.ContextFor(id)!.Pages[0].GetByRole(Microsoft.Playwright.AriaRole.Checkbox).IsCheckedAsync());
            Assert.Null((await Run("browser.type", new { target = await Find("Editable"), text = "Content edited" })).ErrorCode);
            Assert.Equal("Content edited", await browser.ContextFor(id)!.Pages[0].GetByRole(Microsoft.Playwright.AriaRole.Textbox, new() { Name = "Editable" }).InnerTextAsync());
            Assert.Null((await Run("browser.press_key", new { target = await Find("Editable"), key = "Home" })).ErrorCode);
            Assert.Null((await Run("browser.click", new { target = await Find("Category"), clickCount = 1 })).ErrorCode);
            var category = await Run("browser.click", new { target = await Find("Tools"), clickCount = 1 });
            Assert.Contains("Category Tools", category.Observation!.Content!);
            Assert.Null((await Run("browser.select_option", new { target = await Find("Tags"), values = new[] { "one", "two" } })).ErrorCode);
            Assert.Equal(2, await browser.ContextFor(id)!.Pages[0].GetByRole(Microsoft.Playwright.AriaRole.Listbox, new() { Name = "Tags" }).EvaluateAsync<int>("e=>e.selectedOptions.length"));
            var dropped = await Run("browser.drop", new { target = await Find("Drop zone"), text = "Dropped fixture text" });
            Assert.Contains("Dropped fixture text", dropped.Observation!.Content!);
            await browser.ContextFor(id)!.Pages[0].Locator("canvas").ScrollIntoViewIfNeededAsync();
            // Successful image delivery alone does not authorize coordinates. Only
            // read-only capture acquisition may repeat; the mouse call below executes once.
            if (withholdFirstCapture)
                browser.CaptureConsistencyProbe = async page =>
                {
                    browser.CaptureConsistencyProbe = null;
                    await page.EvaluateAsync("() => document.querySelector('canvas').style.border='1px solid black'");
                };
            var screenshot = await CoordinateCaptureAsync(browser, id);
            var box = (await browser.ContextFor(id)!.Pages[0].Locator("canvas").BoundingBoxAsync())!;
            await LogFreshnessAsync(browser, id);
            var mouse = await Run("browser.mouse", new { operation = "click", x = box.X + box.Width / 2, y = box.Y + box.Height / 2, snapshotId = screenshot.Observation!.SnapshotId });
            Assert.True(mouse.ErrorCode is null, $"Mouse error: {mouse.ErrorCode}; effectAttempted={mouse.EffectAttempted}");
            Assert.Contains("Visual target clicked", mouse.Observation!.Content!);
            var waiting = await Find("Waiting action");
            var originalPage = browser.ContextFor(id)!.Pages[0];
            var recoveryUrl = originalPage.Url;
            var recoveryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (withholdFirstCapture)
                await browser.ContextFor(id)!.RouteAsync(recoveryUrl, async route =>
                {
                    recoveryEntered.TrySetResult();
                    await releaseRecovery.Task;
                    try { await route.ContinueAsync(); } catch (Microsoft.Playwright.PlaywrightException) { }
                });
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            browser.ActionStartedProbe = () => started.TrySetResult();
            using var cancel = new CancellationTokenSource();
            var pending = browser.ExecuteAsync(AgentCore.Application.Tools.BrowserToolArguments.Request(id, "browser.click", JsonSerializer.SerializeToElement(new { target = waiting, clickCount = 1 })), cancel.Token).AsTask();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancel.Cancel();
            if (withholdFirstCapture) await recoveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            browser.ActionStartedProbe = null;
            Assert.True(originalPage.IsClosed);
            if (withholdFirstCapture)
            {
                releaseRecovery.TrySetResult();
                await browser.ContextFor(id)!.UnrouteAsync(recoveryUrl);
                Assert.Null((await browser.ExecuteAsync(BrowserTestRequests.Navigate(id, new Uri(recoveryUrl)))).ErrorCode);
            }
            var recovered = await Run("browser.snapshot", new { }); Assert.Null(recovered.ErrorCode);
            Assert.Contains("Nothing selected", recovered.Observation!.Content!);
        }
        finally { releaseRecovery.TrySetResult(); browser.CaptureConsistencyProbe = null; await browser.StopAsync(CancellationToken.None); }
    }
    [Fact]
    public async Task Unadmitted_capture_and_expired_admitted_capture_reject_before_effect_then_fresh_capture_recovers()
    {
        var browser = new NativePlaywrightBrowser(new() { Enabled = true, Headless = true, FixturePort = 0 }, null);
        await browser.StartAsync(default);
        var id = Guid.NewGuid();
        try
        {
            Assert.Null((await browser.ExecuteAsync(BrowserTestRequests.Navigate(id,
                new Uri(browser.HostPolicy.NavigationOrigins.Single() + "/")))).ErrorCode);
            var page = browser.ContextFor(id)!.Pages[0];
            await page.EvaluateAsync("""
                () => {
                    document.body.innerHTML = '<button id="visual">Stable control</button>';
                    window.fixtureClicks = 0;
                    document.querySelector('button').onclick = () => { window.fixtureClicks++; };
                }
                """);
            // Deterministic native mutation inside collection: pixels are delivered,
            // but admission must be withheld before any mouse call is possible.
            browser.CaptureConsistencyProbe = p => p.EvaluateAsync("() => document.querySelector('button').style.marginLeft='20px'");
            var unadmitted = await browser.ExecuteAsync(new(id, new BrowserScreenshot()));
            browser.CaptureConsistencyProbe = null;
            Assert.Null(unadmitted.ErrorCode);
            Assert.NotEmpty(unadmitted.Bytes!);
            output.WriteLine("unadmitted capture: {0}", unadmitted.DataJson);
            using (var receipt = JsonDocument.Parse(unadmitted.DataJson!))
            {
                Assert.False(receipt.RootElement.GetProperty("coordinateEvidence").GetBoolean());
                Assert.Contains("visual_state_changed_during_capture", receipt.RootElement.GetProperty("coordinateEvidenceUnavailableReasons").EnumerateArray().Select(r => r.GetString()));
            }
            await LogFreshnessAsync(browser, id);
            var rejected = await browser.ExecuteAsync(new(id, new BrowserMouse("click", 40, 20, SnapshotId: unadmitted.Observation!.SnapshotId)));
            Assert.Equal("stale_visual_evidence", rejected.ErrorCode);
            Assert.False(rejected.EffectAttempted);
            Assert.Equal(0, await page.EvaluateAsync<int>("() => window.fixtureClicks"));

            var admitted = await CoordinateCaptureAsync(browser, id);
            // This second failure is different: admission succeeded, then the page changed.
            await page.EvaluateAsync("() => document.querySelector('button').style.marginLeft='80px'");
            await LogFreshnessAsync(browser, id);
            var expired = await browser.ExecuteAsync(new(id, new BrowserMouse("click", 40, 20, SnapshotId: admitted.Observation!.SnapshotId)));
            Assert.Equal("stale_visual_evidence", expired.ErrorCode);
            Assert.False(expired.EffectAttempted);
            Assert.Equal(0, await page.EvaluateAsync<int>("() => window.fixtureClicks"));

            var fresh = await CoordinateCaptureAsync(browser, id);
            var box = (await page.GetByRole(Microsoft.Playwright.AriaRole.Button).BoundingBoxAsync())!;
            await LogFreshnessAsync(browser, id);
            var click = new BrowserRequest(id, new BrowserMouse("click", box.X + box.Width / 2, box.Y + box.Height / 2, SnapshotId: fresh.Observation!.SnapshotId));
            var clicked = await browser.ExecuteAsync(click);
            Assert.Null(clicked.ErrorCode);
            Assert.True(clicked.EffectAttempted);
            Assert.Equal(1, await page.EvaluateAsync<int>("() => window.fixtureClicks"));
            // A consumed image must not authorize a second execution of the same action.
            var replay = await browser.ExecuteAsync(click);
            Assert.Equal("stale_visual_evidence", replay.ErrorCode);
            Assert.False(replay.EffectAttempted);
            Assert.Equal(1, await page.EvaluateAsync<int>("() => window.fixtureClicks"));
        }
        finally { browser.CaptureConsistencyProbe = null; await browser.StopAsync(default); }
    }

    private async Task<BrowserResult> CoordinateCaptureAsync(NativePlaywrightBrowser browser, Guid id)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var captured = await browser.ExecuteAsync(new(id, new BrowserScreenshot()), deadline.Token);
            Assert.Null(captured.ErrorCode);
            output.WriteLine("coordinate capture attempt {0}: {1}", attempt, captured.DataJson);
            using var receipt = JsonDocument.Parse(captured.DataJson!);
            if (receipt.RootElement.GetProperty("coordinateEvidence").GetBoolean())
            {
                Assert.True(captured.Observation!.Settled);
                Assert.Empty(receipt.RootElement.GetProperty("coordinateEvidenceUnavailableReasons").EnumerateArray());
                return captured;
            }
            // This retry is explicit, bounded and read-only. No potentially executed
            // action is retried, and an unsettled image never supplies mouse authority.
            Assert.NotEmpty(receipt.RootElement.GetProperty("coordinateEvidenceUnavailableReasons").EnumerateArray());
        }
        Assert.Fail("No admitted coordinate image within three read-only captures and the 20-second deadline.");
        throw new InvalidOperationException();
    }

    private async Task LogFreshnessAsync(NativePlaywrightBrowser browser, Guid id) =>
        output.WriteLine("pre-mouse freshness: {0}", await browser.ContextFor(id)!.Pages[0].EvaluateAsync<string>("""
            () => JSON.stringify({ visualGeneration: window.__acSettle?.visualGeneration,
                domGeneration: window.__acSettle?.generation, inflight: window.__acSettle?.inflight,
                fontsLoading: document.fonts.status !== 'loaded',
                runningAnimations: document.getAnimations().filter(a => a.playState === 'running').length,
                viewport: [innerWidth, innerHeight, scrollX, scrollY] })
            """));

    [Fact]
    public async Task Generic_dialogs_popups_upload_download_screenshots_and_storage_boundaries()
    {
        var browser = new NativePlaywrightBrowser(new BrowserOptions { Enabled = true, Headless = true, FixtureEnabled = true,
            FixturePort = 0, InteractionMode = "InteractiveDemo", NavigationOrigins = ["http://127.0.0.1:5091"], InteractionOrigins = ["http://127.0.0.1:5091"] }, loggerFactory: null);
        await browser.StartAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        try
        {
            var origin = browser.HostPolicy.NavigationOrigins.Single();
            Assert.Null((await browser.ExecuteAsync(BrowserTestRequests.Navigate(id, new Uri(origin + "/browser-native.html?compact=1")))).ErrorCode);
            async Task<BrowserResult> Run(string tool, object args) => await browser.ExecuteAsync(AgentCore.Application.Tools.BrowserToolArguments.Request(id, tool, JsonSerializer.SerializeToElement(args)));
            async Task<BrowserTarget> Find(string name) => (await BrowserTestQueries.Find(browser, id, name)).Target;
            foreach (var (button, operation, prompt) in new[] { ("Alert", "accept", ""), ("Confirm", "dismiss", ""), ("Prompt", "accept", "A label") })
            {
                var reference = await Find(button);
                var clicked = await Run("browser.click", new { target = reference, clickCount = 1 });
                Assert.Equal("dialog_pending", clicked.ErrorCode);
                Assert.Null((await Run("browser.dialog", new { operation = "inspect" })).ErrorCode);
                Assert.Equal("dialog_pending", (await Run("browser.snapshot", new { })).ErrorCode);
                var handled = await Run("browser.dialog", new { operation, promptText = prompt });
                Assert.Null(handled.ErrorCode);
                if (button == "Confirm") Assert.Contains("Dismissed", handled.Observation!.Content!);
                if (button == "Prompt") Assert.Contains("A label", handled.Observation!.Content!);
            }
            var uploaded = await browser.ExecuteAsync(BrowserTestRequests.Interaction(id, BrowserOperation.Upload, await Find("Upload file"), null, Uploads: [new("note.txt", "text/plain", System.Text.Encoding.UTF8.GetBytes("Approved resource"))]));
            Assert.Null(uploaded.ErrorCode); Assert.Contains("note.txt", uploaded.Observation!.Content!);
            var downloaded = await Run("browser.click", new { target = await Find("Download note"), clickCount = 1 });
            Assert.Null(downloaded.ErrorCode);
            var file = Assert.Single(downloaded.Downloads!); Assert.Null(file.ErrorCode);
            Assert.Equal("browser-note.txt", file.FileName); Assert.Contains("Generic browser fixture note", System.Text.Encoding.UTF8.GetString(file.Bytes!));
            foreach (var format in new[] { "png", "jpeg", "webp" })
            {
                var screenshot = await Run("browser.screenshot", new { format });
                Assert.Null(screenshot.ErrorCode); Assert.NotEmpty(screenshot.Bytes!); Assert.Equal("image/" + format, screenshot.ContentType);
            }
            var storage = await Run("browser.local_storage", new { operation = "get", key = "access_token" });
            Assert.DoesNotContain("fixture-protected-token", storage.DataJson!); Assert.Contains("redacted", storage.DataJson!);
            Assert.Equal("forbidden", (await Run("browser.local_storage", new { operation = "set", key = "access_token", value = "replacement" })).ErrorCode);
            Assert.Null((await Run("browser.session_storage", new { operation = "set", key = "draft", value = "Visible draft" })).ErrorCode);
            Assert.Equal("Visible draft", await browser.ContextFor(id)!.Pages[0].EvaluateAsync<string>("() => sessionStorage.getItem('draft')"));
            Assert.DoesNotContain("Visible draft", (await Run("browser.session_storage", new { operation = "get", key = "draft" })).DataJson!);
            Assert.Equal("target_denied", (await Run("browser.route", new { url = "https://example.invalid/mock", action = "abort" })).ErrorCode);
            var rule = await Run("browser.route", new { url = origin + "/browser-native-data", action = "fulfill", body = "Mocked fixture data" }); Assert.Null(rule.ErrorCode);
            var inventory = await Run("browser.routes", new { }); Assert.Null(inventory.ErrorCode);
            using var createdRule = JsonDocument.Parse(rule.DataJson!);
            using var inventoryData = JsonDocument.Parse(inventory.DataJson!);
            var ruleRef = createdRule.RootElement.GetProperty("ruleRef").GetString();
            Assert.Equal(ruleRef, Assert.Single(inventoryData.RootElement.GetProperty("rules").EnumerateArray()).GetProperty("ruleRef").GetString());
            Assert.False(inventory.EffectAttempted);
            var fetched = await Run("browser.click", new { target = await Find("Fetch data"), clickCount = 1 }); Assert.Null(fetched.ErrorCode);
            var ready = await Run("browser.wait_for", new { condition = "text", text = "Mocked fixture data" }); Assert.Null(ready.ErrorCode);
            Assert.Contains("Mocked fixture data", ready.Observation!.Content!);
            Assert.Null((await Run("browser.unroute", new { ruleRef })).ErrorCode);
            using var removedRules = JsonDocument.Parse((await Run("browser.routes", new { })).DataJson!);
            Assert.Empty(removedRules.RootElement.GetProperty("rules").EnumerateArray());
            Assert.Null((await Run("browser.click", new { target = await Find("Fetch data"), clickCount = 1 })).ErrorCode);
            Assert.Null((await Run("browser.wait_for", new { condition = "text", text = "Async fixture loaded" })).ErrorCode);
            var denied = await browser.ExecuteAsync(BrowserTestRequests.Interaction(id, BrowserOperation.Click, await Find("Denied popup"), null)); Assert.Equal("target_denied", denied.ErrorCode);
            var popup = await browser.ExecuteAsync(BrowserTestRequests.Interaction(id, BrowserOperation.Click, await Find("Popup"), null)); Assert.Null(popup.ErrorCode);
            using var tabs = JsonDocument.Parse((await Run("browser.tabs", new { operation = "list" })).DataJson!);
            Assert.Equal(2, tabs.RootElement.GetProperty("tabs").GetArrayLength());
            var original = tabs.RootElement.GetProperty("tabs").EnumerateArray().First(t => t.GetProperty("active").GetBoolean()).GetProperty("tabRef").GetString()!;
            var opened = tabs.RootElement.GetProperty("tabs").EnumerateArray().First(t => !t.GetProperty("active").GetBoolean()).GetProperty("tabRef").GetString()!;
            Assert.Null((await Run("browser.tabs", new { operation = "select", tabRef = opened })).ErrorCode);
            Assert.Null((await Run("browser.tabs", new { operation = "close", tabRef = opened })).ErrorCode);
            Assert.Equal("stale_tab", (await Run("browser.tabs", new { operation = "select", tabRef = opened })).ErrorCode);
            Assert.Null((await Run("browser.tabs", new { operation = "select", tabRef = original })).ErrorCode);
        }
        finally { await browser.StopAsync(CancellationToken.None); }
    }
    [Fact]
    public async Task Generic_spa_search_rerender_frames_forms_tabs_and_redacted_diagnostics()
    {
        var browser = new NativePlaywrightBrowser(new BrowserOptions { Enabled = true, Headless = true,
            FixtureEnabled = true, FixturePort = 0, InteractionMode = "InteractiveDemo", NavigationOrigins = ["http://127.0.0.1:5091"], InteractionOrigins = ["http://127.0.0.1:5091"] }, loggerFactory: null);
        await browser.StartAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        try
        {
            var origin = browser.HostPolicy.NavigationOrigins.Single();
            var navigation = await browser.ExecuteAsync(BrowserTestRequests.Navigate(id, new Uri(origin + "/browser-native.html")));
            Assert.Null(navigation.ErrorCode); Assert.NotNull(navigation.Observation!.SnapshotId);
            Assert.Empty(navigation.Observation.Targets);
            Assert.Contains("Asset", navigation.Observation.Content);
            Assert.True(navigation.Observation.Content!.Length <= 8000);
            async Task<BrowserResult> Run(string name, object args)
            { using var doc = JsonDocument.Parse(JsonSerializer.Serialize(args)); return await browser.ExecuteAsync(AgentCore.Application.Tools.BrowserToolArguments.Request(id, name, doc.RootElement.Clone())); }
            async Task<BrowserTarget> Find(string name) => (await BrowserTestQueries.Find(browser, id, name)).Target;
            var item = await Find("Asset 159");
            // Replace the DOM node after discovery: the ref must resolve by native locator.
            await browser.ContextFor(id)!.Pages[0].GetByRole(Microsoft.Playwright.AriaRole.Treeitem,
                new() { Name = "Asset 159", Exact = true }).EvaluateAsync("el => { const clone=el.cloneNode(true); clone.onclick=el.onclick; el.replaceWith(clone); }");
            var clicked = await browser.ExecuteAsync(BrowserTestRequests.Interaction(id, BrowserOperation.Click, item, null));
            Assert.Null(clicked.ErrorCode); Assert.Contains("Selected Asset 159", clicked.Observation!.Content!);
            Assert.Null((await browser.ExecuteAsync(BrowserTestRequests.Interaction(id, BrowserOperation.Click, item, null))).ErrorCode);
            var scoped = await Run("browser.snapshot", new { target = await Find("Assets"), depth = 2, boxes = true });
            Assert.Null(scoped.ErrorCode); Assert.NotEmpty(scoped.Observation!.Boxes!);
            Assert.Contains("tree \"Assets\"", scoped.Observation.Content);
            Assert.DoesNotContain("Title", scoped.Observation.Content);
            Assert.NotNull(await Find("Title"));
            var frameRef = Assert.Single(scoped.Observation.Frames!).Ref;
            var frameFound = await Run("browser.find", new { target = new { by = "role", value = "button", name = "Save frame", frameRef } });
            Assert.Null(frameFound.ErrorCode);
            var frame = JsonDocument.Parse(frameFound.DataJson!).RootElement.GetProperty("matches")[0].GetProperty("target").Deserialize<BrowserTarget>(JsonSerializerOptions.Web)!;
            var saved = await browser.ExecuteAsync(BrowserTestRequests.Interaction(id, BrowserOperation.Click, frame, null)); Assert.Null(saved.ErrorCode);
            Assert.Contains("Frame saved", await browser.ContextFor(id)!.Pages[0].Frames.Single(f => f != browser.ContextFor(id)!.Pages[0].MainFrame).Locator("body").InnerTextAsync());
            var title = await Find("Title"); var notes = await Find("Notes");
            var filled = await Run("browser.fill_form", new { fields = new[] { new { target = title, value = "Example" }, new { target = notes, value = "Notes written" } } });
            Assert.Null(filled.ErrorCode); Assert.Equal("Example",(await BrowserTestQueries.Find(browser, id, "Title")).State!.Value);
            var key = await Run("browser.press_key", new { key = "Shift+Tab" }); Assert.Null(key.ErrorCode);
            var tabs = await Run("browser.tabs", new { operation = "new", url = origin + "/browser-native.html?new=1" }); Assert.Null(tabs.ErrorCode);
            var listed = await Run("browser.tabs", new { operation = "list" }); using var list = JsonDocument.Parse(listed.DataJson!);
            Assert.Equal(2,list.RootElement.GetProperty("tabs").GetArrayLength());
            var logs = await Run("browser.console_messages", new { }); Assert.Null(logs.ErrorCode); Assert.DoesNotContain("fixture-protected-token", logs.DataJson!);
            Assert.False(AgentCore.Application.Tools.BrowserToolCatalog.TryGet("browser.evaluate", out _));
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await browser.ExecuteAsync(AgentCore.Application.Tools.BrowserToolArguments.Request(id,"browser.snapshot",JsonSerializer.SerializeToElement(new {})), cancel.Token));
            Assert.Null((await Run("browser.snapshot",new {})).ErrorCode);
        }
        finally { await browser.StopAsync(CancellationToken.None); }
    }
}
