using AgentCore.Tests.Shared;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Browser;
namespace AgentCore.Infrastructure.Tests;

[Collection(BrowserChromiumCollection.Name)]
public sealed class NativeBrowserJourneyTests
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
            await page.Locator("iframe").EvaluateAsync("(frame,url)=>frame.src=url", readOnly.Origin + "/browser-native-frame.html");
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
    [Fact]
    public async Task Native_forms_keyboard_visual_target_and_pending_action_cancellation_recover()
    {
        var browser = new NativePlaywrightBrowser(new BrowserOptions { Enabled = true, Headless = true, FixtureEnabled = true,
            FixturePort = 0, InteractionMode = "InteractiveDemo", NavigationOrigins = ["http://127.0.0.1:5091"], InteractionOrigins = ["http://127.0.0.1:5091"] }, loggerFactory: null);
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
            var box = (await browser.ContextFor(id)!.Pages[0].Locator("canvas").BoundingBoxAsync())!;
            Assert.Null((await Run("browser.screenshot", new { format = "png" })).ErrorCode);
            var mouse = await Run("browser.mouse", new { operation = "click", x = box.X + box.Width / 2, y = box.Y + box.Height / 2 });
            Assert.Null(mouse.ErrorCode);
            Assert.Contains("Visual target clicked", mouse.Observation!.Content!);
            var waiting = await Find("Waiting action");
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            browser.ActionStartedProbe = () => started.TrySetResult();
            using var cancel = new CancellationTokenSource();
            var pending = browser.ExecuteAsync(AgentCore.Application.Tools.BrowserToolArguments.Request(id, "browser.click", JsonSerializer.SerializeToElement(new { target = waiting, clickCount = 1 })), cancel.Token).AsTask();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            browser.ActionStartedProbe = null;
            var recovered = await Run("browser.snapshot", new { }); Assert.Null(recovered.ErrorCode);
            Assert.Contains("Nothing selected", recovered.Observation!.Content!);
        }
        finally { await browser.StopAsync(CancellationToken.None); }
    }
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
            var fetched = await Run("browser.click", new { target = await Find("Fetch data"), clickCount = 1 }); Assert.Null(fetched.ErrorCode);
            var ready = await Run("browser.wait_for", new { condition = "text", text = "Mocked fixture data" }); Assert.Null(ready.ErrorCode);
            Assert.Contains("Mocked fixture data", ready.Observation!.Content!);
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
