using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Browser;
namespace AgentCore.Infrastructure.Tests;

public sealed class BrowserV2JourneyTests
{
    [Fact]
    public async Task Readable_frame_ref_cannot_bypass_the_interaction_origin_policy()
    {
        await using var main = new LoopbackBrowserFixtureHost(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        await using var readOnly = new LoopbackBrowserFixtureHost(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        await main.StartAsync(0, CancellationToken.None); await readOnly.StartAsync(0, CancellationToken.None);
        var browser = new PlaywrightBrowser(new BrowserOptions { Enabled = true, Headless = true, FixtureEnabled = false,
            InteractionMode = "InteractiveDemo", NavigationOrigins = [main.Origin!, readOnly.Origin!], InteractionOrigins = [main.Origin!] }, loggerFactory: null);
        await browser.StartAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        try
        {
            Assert.All(AgentCore.Application.Tools.BrowserToolCatalog.Tools.Values.Where(t => t.Group == "core"), t => Assert.True(browser.Provider.Supports(t.Feature), t.Name));
            Assert.Null((await browser.NavigateAsync(new(id, new Uri(main.Origin! + "/browser-v2.html?compact=1")))).ErrorCode);
            var page = browser.ContextFor(id)!.Pages[0];
            await page.Locator("iframe").EvaluateAsync("(frame,url)=>frame.src=url", readOnly.Origin + "/browser-v2-frame.html");
            await page.FrameLocator("iframe").GetByRole(Microsoft.Playwright.AriaRole.Button, new() { Name = "Save frame" }).WaitForAsync();
            var snapshot = await browser.SnapshotAsync(id); Assert.Null(snapshot.ErrorCode);
            var reference = snapshot.Observation!.Elements.Single(e => e.Name == "Save frame").Ref;
            Assert.Equal("target_denied", (await browser.InteractAsync(new(id, "click", reference, null))).ErrorCode);
            Assert.Equal("target_denied", (await browser.ExecuteAsync(new(id, "browser.click", JsonSerializer.SerializeToElement(new { @ref = reference, clickCount = 1 })))).ErrorCode);
            Assert.Equal("Save frame", await page.FrameLocator("iframe").GetByRole(Microsoft.Playwright.AriaRole.Button).InnerTextAsync());
        }
        finally { await browser.StopAsync(CancellationToken.None); }
    }
    [Fact]
    public async Task Native_forms_keyboard_visual_target_and_pending_action_cancellation_recover()
    {
        var browser = new PlaywrightBrowser(new BrowserOptions { Enabled = true, Headless = true, FixtureEnabled = true,
            FixturePort = 0, InteractionMode = "InteractiveDemo", NavigationOrigins = ["http://127.0.0.1:5091"], InteractionOrigins = ["http://127.0.0.1:5091"] }, loggerFactory: null);
        await browser.StartAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        try
        {
            Assert.Null((await browser.NavigateAsync(new(id, new Uri(browser.HostPolicy.NavigationOrigins.Single() + "/browser-v2.html?compact=1")))).ErrorCode);
            async Task<BrowserCommandResult> Run(string tool, object args) => await browser.ExecuteAsync(new(id, tool, JsonSerializer.SerializeToElement(args)));
            async Task<string> Find(string name)
            {
                using var found = JsonDocument.Parse((await Run("browser.find", new { text = name })).DataJson!);
                return found.RootElement.GetProperty("matches").EnumerateArray().First(m => m.GetProperty("name").GetString() == name).GetProperty("ref").GetString()!;
            }
            Assert.Null((await Run("browser.fill_form", new { fields = new[] { new { @ref = await Find("Enabled"), @checked = true } } })).ErrorCode);
            Assert.True(await browser.ContextFor(id)!.Pages[0].GetByRole(Microsoft.Playwright.AriaRole.Checkbox).IsCheckedAsync());
            Assert.Null((await Run("browser.type", new { @ref = await Find("Editable"), text = "Content edited" })).ErrorCode);
            Assert.Equal("Content edited", await browser.ContextFor(id)!.Pages[0].GetByRole(Microsoft.Playwright.AriaRole.Textbox, new() { Name = "Editable" }).InnerTextAsync());
            Assert.Null((await Run("browser.press_key", new { @ref = await Find("Editable"), key = "Home" })).ErrorCode);
            Assert.Null((await Run("browser.click", new { @ref = await Find("Category"), clickCount = 1 })).ErrorCode);
            var category = await Run("browser.click", new { @ref = await Find("Tools"), clickCount = 1 });
            Assert.Contains("Category Tools", category.Snapshot!.VisibleText);
            Assert.Null((await Run("browser.select_option", new { @ref = await Find("Tags"), values = new[] { "one", "two" } })).ErrorCode);
            Assert.Equal(2, await browser.ContextFor(id)!.Pages[0].GetByRole(Microsoft.Playwright.AriaRole.Listbox, new() { Name = "Tags" }).EvaluateAsync<int>("e=>e.selectedOptions.length"));
            var dropped = await Run("browser.drop", new { @ref = await Find("Drop zone"), text = "Dropped fixture text" });
            Assert.Contains("Dropped fixture text", dropped.Snapshot!.VisibleText);
            await browser.ContextFor(id)!.Pages[0].Locator("canvas").ScrollIntoViewIfNeededAsync();
            var box = (await browser.ContextFor(id)!.Pages[0].Locator("canvas").BoundingBoxAsync())!;
            Assert.Null((await Run("browser.screenshot", new { format = "png" })).ErrorCode);
            var mouse = await Run("browser.mouse", new { operation = "click", x = box.X + box.Width / 2, y = box.Y + box.Height / 2 });
            Assert.Null(mouse.ErrorCode);
            Assert.Contains("Visual target clicked", mouse.Snapshot!.VisibleText);
            var waiting = await Find("Waiting action");
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            browser.ActionStartedProbe = () => started.TrySetResult();
            using var cancel = new CancellationTokenSource();
            var pending = browser.ExecuteAsync(
                new(id, "browser.click", JsonSerializer.SerializeToElement(new { @ref = waiting, clickCount = 1 })), cancel.Token).AsTask();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            browser.ActionStartedProbe = null;
            Assert.Equal("stale_reference", (await Run("browser.click", new { @ref = waiting, clickCount = 1 })).ErrorCode);
            var recovered = await Run("browser.snapshot", new { }); Assert.Null(recovered.ErrorCode);
            Assert.Contains("Nothing selected", recovered.Snapshot!.VisibleText);
        }
        finally { await browser.StopAsync(CancellationToken.None); }
    }
    [Fact]
    public async Task Generic_dialogs_popups_upload_download_screenshots_and_storage_boundaries()
    {
        var browser = new PlaywrightBrowser(new BrowserOptions { Enabled = true, Headless = true, FixtureEnabled = true,
            FixturePort = 0, InteractionMode = "InteractiveDemo", NavigationOrigins = ["http://127.0.0.1:5091"], InteractionOrigins = ["http://127.0.0.1:5091"] }, loggerFactory: null);
        await browser.StartAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        try
        {
            var origin = browser.HostPolicy.NavigationOrigins.Single();
            Assert.Null((await browser.NavigateAsync(new(id, new Uri(origin + "/browser-v2.html?compact=1")))).ErrorCode);
            async Task<BrowserCommandResult> Run(string tool, object args) => await browser.ExecuteAsync(new(id, tool, JsonSerializer.SerializeToElement(args)));
            async Task<string> Find(string name)
            {
                using var found = JsonDocument.Parse((await Run("browser.find", new { text = name })).DataJson!);
                return found.RootElement.GetProperty("matches").EnumerateArray().First(m => m.GetProperty("name").GetString() == name).GetProperty("ref").GetString()!;
            }
            foreach (var (button, operation, prompt) in new[] { ("Alert", "accept", ""), ("Confirm", "dismiss", ""), ("Prompt", "accept", "A label") })
            {
                var reference = await Find(button);
                var clicked = await Run("browser.click", new { @ref = reference, clickCount = 1 });
                Assert.Equal("dialog_pending", clicked.ErrorCode);
                Assert.Null((await Run("browser.dialog", new { operation = "inspect" })).ErrorCode);
                Assert.Equal("dialog_pending", (await Run("browser.snapshot", new { })).ErrorCode);
                var handled = await Run("browser.dialog", new { operation, promptText = prompt });
                Assert.Null(handled.ErrorCode);
                if (button == "Confirm") Assert.Contains("Dismissed", handled.Snapshot!.VisibleText);
                if (button == "Prompt") Assert.Contains("A label", handled.Snapshot!.VisibleText);
            }
            var uploaded = await browser.InteractAsync(new(id, "upload", await Find("Upload file"), null,
                Uploads: [new("note.txt", "text/plain", System.Text.Encoding.UTF8.GetBytes("Approved resource"))]));
            Assert.Null(uploaded.ErrorCode); Assert.Contains("note.txt", uploaded.Observation!.VisibleText);
            var downloaded = await Run("browser.click", new { @ref = await Find("Download note"), clickCount = 1 });
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
            var rule = await Run("browser.route", new { url = origin + "/browser-v2-data", action = "fulfill", body = "Mocked fixture data" }); Assert.Null(rule.ErrorCode);
            var fetched = await Run("browser.click", new { @ref = await Find("Fetch data"), clickCount = 1 }); Assert.Null(fetched.ErrorCode);
            var ready = await Run("browser.wait_for", new { condition = "text", text = "Mocked fixture data" }); Assert.Null(ready.ErrorCode);
            Assert.Contains("Mocked fixture data", ready.Snapshot!.VisibleText);
            var denied = await browser.InteractAsync(new(id, "click", await Find("Denied popup"), null)); Assert.Equal("target_denied", denied.ErrorCode);
            var popup = await browser.InteractAsync(new(id, "click", await Find("Popup"), null)); Assert.Null(popup.ErrorCode);
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
        var browser = new PlaywrightBrowser(new BrowserOptions { Enabled = true, Headless = true,
            FixtureEnabled = true, FixturePort = 0, InteractionMode = "InteractiveDemo", NavigationOrigins = ["http://127.0.0.1:5091"], InteractionOrigins = ["http://127.0.0.1:5091"] }, loggerFactory: null);
        await browser.StartAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        try
        {
            var origin = browser.HostPolicy.NavigationOrigins.Single();
            var navigation = await browser.NavigateAsync(new(id, new Uri(origin + "/browser-v2.html")));
            Assert.Null(navigation.ErrorCode); Assert.NotNull(navigation.Observation!.SnapshotId);
            Assert.True(navigation.Observation.Elements.Count > 160);
            Assert.True(navigation.Observation.Content!.Length <= 8000);
            async Task<BrowserCommandResult> Run(string name, object args)
            { using var doc = JsonDocument.Parse(JsonSerializer.Serialize(args)); return await browser.ExecuteAsync(new(id, name, doc.RootElement.Clone())); }
            async Task<string> Find(string name)
            {
                var found = await Run("browser.find", new { text = name }); Assert.Null(found.ErrorCode);
                using var doc = JsonDocument.Parse(found.DataJson!);
                return doc.RootElement.GetProperty("matches").EnumerateArray().First(m => m.GetProperty("name").GetString() == name).GetProperty("ref").GetString()!;
            }
            var item = await Find("Asset 159");
            // Replace the DOM node after discovery: the ref must resolve by native locator.
            await browser.ContextFor(id)!.Pages[0].GetByRole(Microsoft.Playwright.AriaRole.Treeitem,
                new() { Name = "Asset 159", Exact = true }).EvaluateAsync("el => { const clone=el.cloneNode(true); clone.onclick=el.onclick; el.replaceWith(clone); }");
            var clicked = await browser.InteractAsync(new(id, "click", item, null));
            Assert.Null(clicked.ErrorCode); Assert.Contains("Selected Asset 159", clicked.Observation!.VisibleText);
            Assert.Equal("stale_reference", (await browser.InteractAsync(new(id,"click",item,null))).ErrorCode);
            var scoped = await Run("browser.snapshot", new { targetRef = await Find("Assets"), depth = 2, boxes = true });
            Assert.Null(scoped.ErrorCode); Assert.NotEmpty(scoped.Snapshot!.Boxes!);
            Assert.Contains("tree \"Assets\"", scoped.Snapshot.Content);
            Assert.DoesNotContain("Title", scoped.Snapshot.Content);
            Assert.NotEmpty(await Find("Title"));
            var frame = await Find("Save frame");
            var saved = await browser.InteractAsync(new(id,"click",frame,null)); Assert.Null(saved.ErrorCode);
            Assert.NotEmpty(await Find("Frame saved"));
            var title = await Find("Title"); var notes = await Find("Notes");
            var filled = await Run("browser.fill_form", new { fields = new[] { new { @ref = title, value = "Example" }, new { @ref = notes, value = "Notes written" } } });
            Assert.Null(filled.ErrorCode); Assert.Equal("Example",filled.Snapshot!.Elements.Single(e => e.Name == "Title").State!.Value);
            var key = await Run("browser.press_key", new { key = "Shift+Tab" }); Assert.Null(key.ErrorCode);
            var tabs = await Run("browser.tabs", new { operation = "new", url = origin + "/browser-v2.html?new=1" }); Assert.Null(tabs.ErrorCode);
            var listed = await Run("browser.tabs", new { operation = "list" }); using var list = JsonDocument.Parse(listed.DataJson!);
            Assert.Equal(2,list.RootElement.GetProperty("tabs").GetArrayLength());
            var logs = await Run("browser.console_messages", new { }); Assert.Null(logs.ErrorCode); Assert.DoesNotContain("fixture-protected-token", logs.DataJson!);
            Assert.Equal("unsupported_operation", (await Run("browser.evaluate",new { script="() => localStorage.access_token" })).ErrorCode);
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await browser.ExecuteAsync(new(id,"browser.snapshot",JsonSerializer.SerializeToElement(new {})), cancel.Token));
            Assert.Null((await Run("browser.snapshot",new {})).ErrorCode);
        }
        finally { await browser.StopAsync(CancellationToken.None); }
    }
}
