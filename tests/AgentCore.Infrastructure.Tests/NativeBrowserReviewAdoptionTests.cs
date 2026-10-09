using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Infrastructure.Browser;
using AgentCore.Tests.Shared;
using Microsoft.Playwright;

namespace AgentCore.Infrastructure.Tests;

[Collection(BrowserChromiumCollection.Name)]
public sealed class NativeBrowserReviewAdoptionTests
{
    [Fact]
    public async Task Denied_popup_is_closed_before_the_tool_releases_its_session_gate()
    {
        var browser = new NativePlaywrightBrowser(new() { Enabled = true, Headless = true, FixturePort = 0 }, null);
        await browser.StartAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Assert.Null((await browser.ExecuteAsync(BrowserTestRequests.Navigate(id,
                new Uri(browser.HostPolicy.NavigationOrigins.Single() + "/browser-native.html?compact=1")))).ErrorCode);
            var context = browser.ContextFor(id)!;
            var original = context.Pages[0];
            var priorBlank = await context.NewPageAsync();
            await original.SetContentAsync("<button>Open denied</button>");
            await original.EvaluateAsync("() => document.querySelector('button').addEventListener('click', () => window.open('https://example.invalid/denied'))");
            var reference = (await BrowserTestQueries.Find(browser, id, "Open denied")).Ref;
            browser.DeniedPopupCloseProbe = async page => { entered.TrySetResult(); await release.Task; await page.CloseAsync(); };
            browser.PopupCleanupWaitProbe = () => waiting.TrySetResult();
            var pending = browser.ExecuteAsync(BrowserToolArguments.Request(id, "browser.click",
                JsonSerializer.SerializeToElement(new { @ref = reference }))).AsTask();
            var started = await Task.WhenAny(entered.Task, pending).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(ReferenceEquals(started, entered.Task), "Tool ended before denied-popup cleanup: " + (pending.IsCompletedSuccessfully ? (await pending).ErrorCode : pending.Status.ToString()));
            var observed = await Task.WhenAny(waiting.Task, pending).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(ReferenceEquals(observed, waiting.Task), "Tool completed before cleanup wait: " + (pending.IsCompletedSuccessfully ? (await pending).ErrorCode : pending.Status.ToString()));
            Assert.False(pending.IsCompleted);
            release.TrySetResult();
            Assert.Equal("target_denied", (await pending).ErrorCode);
            Assert.Equal(2, context.Pages.Count);
            Assert.Contains(original, context.Pages);
            Assert.Contains(priorBlank, context.Pages);
            Assert.Null((await browser.ExecuteAsync(BrowserTestRequests.Inspect(id))).ErrorCode);
        }
        finally { release.TrySetResult(); await browser.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Canceled_activation_fences_the_selected_page_before_a_late_native_call()
    {
        var browser = new NativePlaywrightBrowser(new() { Enabled = true, Headless = true, FixturePort = 0 }, null);
        await browser.StartAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? native = null;
        try
        {
            var url = browser.HostPolicy.NavigationOrigins.Single() + "/browser-native.html?compact=1";
            Assert.Null((await browser.ExecuteAsync(BrowserTestRequests.Navigate(id, new Uri(url)))).ErrorCode);
            var context = browser.ContextFor(id)!;
            var original = context.Pages[0];
            var second = await context.NewPageAsync();
            await second.GotoAsync(url);
            var list = await browser.ExecuteAsync(BrowserToolArguments.Request(id, "browser.tabs",
                JsonSerializer.SerializeToElement(new { operation = "list" })));
            var selectedRef = list.Pages!.Single(p => !p.Active).PageId;
            browser.ActivateTabProbe = page => native = Delayed(page);
            async Task Delayed(IPage page) { entered.TrySetResult(); await release.Task; await page.BringToFrontAsync(); }
            using var cancel = new CancellationTokenSource();
            var pending = browser.ExecuteAsync(BrowserToolArguments.Request(id, "browser.tabs",
                JsonSerializer.SerializeToElement(new { operation = "select", tabRef = selectedRef })), cancel.Token).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.True(second.IsClosed);
            Assert.False(original.IsClosed);
            release.TrySetResult();
            await Assert.ThrowsAnyAsync<PlaywrightException>(() => native!);
            browser.ActivateTabProbe = null;
            Assert.Null((await browser.ExecuteAsync(BrowserTestRequests.Inspect(id))).ErrorCode);
            Assert.Equal("stale_tab", (await browser.ExecuteAsync(BrowserToolArguments.Request(id, "browser.tabs",
                JsonSerializer.SerializeToElement(new { operation = "select", tabRef = selectedRef })))).ErrorCode);
        }
        finally { release.TrySetResult(); await browser.StopAsync(CancellationToken.None); }
    }

    [Theory]
    [InlineData("row")]
    [InlineData("group")]
    public async Task Repeated_controls_resolve_through_unique_native_container_text_and_survive_rerender(string role)
    {
        var browser = new NativePlaywrightBrowser(new() { Enabled = true, Headless = true, FixturePort = 0 }, null);
        await browser.StartAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        try
        {
            Assert.Null((await browser.ExecuteAsync(BrowserTestRequests.Navigate(id,
                new Uri(browser.HostPolicy.NavigationOrigins.Single() + "/browser-native.html?compact=1")))).ErrorCode);
            var page = browser.ContextFor(id)!.Pages[0];
            await page.SetContentAsync("""
                <div role="row"><span>Record A</span><button>Edit</button><button>Delete</button></div>
                <div role="row"><span>Record B</span><button>Edit</button><button>Delete</button></div>
                <p role="status">Unchanged</p>
                <script>document.addEventListener('click', e => {
                    if (e.target.tagName === 'BUTTON') document.querySelector('[role=status]').textContent =
                        e.target.parentElement.querySelector('span').textContent + ' ' + e.target.textContent;
                });</script>
                """.Replace("role=\"row\"", $"role=\"{role}\""));
            var nativeRole = Enum.Parse<AriaRole>(role, true);
            async Task<BrowserResult> Run(string tool, object args) => await browser.ExecuteAsync(
                BrowserToolArguments.Request(id, tool, JsonSerializer.SerializeToElement(args)));
            static string Ref(BrowserResult result)
            {
                Assert.Null(result.ErrorCode);
                using var json = JsonDocument.Parse(result.DataJson!);
                return json.RootElement.GetProperty("matches")[0].GetProperty("ref").GetString()!;
            }
            var duplicate = await Run("browser.find", new { role = "button", name = "Edit" });
            Assert.Equal("ambiguous_target", duplicate.ErrorCode);
            Assert.DoesNotContain("\"ref\":", duplicate.DataJson!);
            var row = Ref(await Run("browser.find", new { role, hasText = "Record B" }));
            var edit = Ref(await Run("browser.find", new { role = "button", name = "Edit", scopeRef = row }));
            await page.GetByRole(nativeRole).Filter(new() { HasText = "Record B" })
                .EvaluateAsync("row => row.replaceWith(row.cloneNode(true))");
            Assert.Null((await Run("browser.click", new { @ref = edit })).ErrorCode);
            Assert.Equal("Record B Edit", await page.GetByRole(AriaRole.Status).InnerTextAsync());
            var delete = Ref(await Run("browser.find", new { role = "button", name = "Delete", scopeRef = row }));
            Assert.Null((await Run("browser.click", new { @ref = delete })).ErrorCode);
            Assert.Equal("Record B Delete", await page.GetByRole(AriaRole.Status).InnerTextAsync());
            var stillAmbiguous = await Run("browser.find", new { role, hasText = "Record" });
            Assert.Equal("ambiguous_target", stillAmbiguous.ErrorCode);
            Assert.DoesNotContain("\"ref\":", stillAmbiguous.DataJson!);
            Assert.Equal("not_found", (await Run("browser.find", new { role, hasText = "Record.*B" })).ErrorCode);
            await page.GetByRole(nativeRole).Filter(new() { HasText = "Record B" })
                .EvaluateAsync("row => row.after(row.cloneNode(true))");
            Assert.Equal("ambiguous_target", (await Run("browser.click", new { @ref = edit })).ErrorCode);
            Assert.Equal("Record B Delete", await page.GetByRole(AriaRole.Status).InnerTextAsync());
        }
        finally { await browser.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Selecting_a_tab_activates_the_native_page_and_retains_session_ownership()
    {
        var headed = Environment.GetEnvironmentVariable("AGENTCORE_BROWSER_HEADED_REVIEW") == "1";
        var browser = new NativePlaywrightBrowser(new() { Enabled = true, Headless = !headed, FixturePort = 0 }, null);
        await browser.StartAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        try
        {
            var url = browser.HostPolicy.NavigationOrigins.Single() + "/browser-native.html?compact=1";
            Assert.Null((await browser.ExecuteAsync(BrowserTestRequests.Navigate(id, new Uri(url)))).ErrorCode);
            var context = browser.ContextFor(id)!;
            var original = context.Pages[0];
            var second = await context.NewPageAsync();
            await second.GotoAsync(url);
            if (headed)
            {
                // Playwright emulates focus on every page. Disable that test-only
                // emulation so the headed check observes actual foreground focus.
                var originalProtocol = await context.NewCDPSessionAsync(original);
                var secondProtocol = await context.NewCDPSessionAsync(second);
                await originalProtocol.SendAsync("Emulation.setFocusEmulationEnabled", new Dictionary<string, object> { ["enabled"] = false });
                await secondProtocol.SendAsync("Emulation.setFocusEmulationEnabled", new Dictionary<string, object> { ["enabled"] = false });
            }
            await second.BringToFrontAsync();
            if (headed)
            {
                Assert.False(await original.EvaluateAsync<bool>("() => document.hasFocus()"));
                Assert.True(await second.EvaluateAsync<bool>("() => document.hasFocus()"));
            }
            var listed = await browser.ExecuteAsync(BrowserToolArguments.Request(id, "browser.tabs",
                JsonSerializer.SerializeToElement(new { operation = "list" })));
            Assert.Null(listed.ErrorCode);
            var originalRef = listed.Pages!.Single(p => p.Active).PageId;
            var foreign = await browser.ExecuteAsync(BrowserToolArguments.Request(Guid.NewGuid(), "browser.tabs",
                JsonSerializer.SerializeToElement(new { operation = "select", tabRef = originalRef })));
            Assert.NotNull(foreign.ErrorCode);
            IPage? activated = null;
            browser.ActivateTabProbe = page => { activated = page; return page.BringToFrontAsync(); };
            var selected = await browser.ExecuteAsync(BrowserToolArguments.Request(id, "browser.tabs",
                JsonSerializer.SerializeToElement(new { operation = "select", tabRef = originalRef })));
            Assert.Null(selected.ErrorCode);
            Assert.Equal(originalRef, selected.Observation!.TabRef);
            Assert.Same(original, activated);
            if (headed)
            {
                Assert.True(await original.EvaluateAsync<bool>("() => document.hasFocus()"));
                Assert.False(await second.EvaluateAsync<bool>("() => document.hasFocus()"));
            }
        }
        finally { await browser.StopAsync(CancellationToken.None); }
    }
}
