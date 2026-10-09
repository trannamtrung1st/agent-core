using System.Text.Json;
using System.Diagnostics;
using Xunit.Abstractions;
using Microsoft.Extensions.Time.Testing;
using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Browser;
using AgentCore.Tests.Shared;
using Microsoft.Playwright;

namespace AgentCore.Infrastructure.Tests;

[Collection(BrowserChromiumCollection.Name)]
public sealed class BrowserReliabilityJourneyTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Repeated_discovery_has_no_ref_registry_and_targets_always_resolve_current_owned_page()
    {
        var browser = Create(); await browser.StartAsync(default); var id = Guid.NewGuid(); var other = Guid.NewGuid();
        try
        {
            var url = new Uri(browser.HostPolicy.NavigationOrigins.Single() + "/browser-native.html?compact=1");
            await browser.ExecuteAsync(BrowserTestRequests.Navigate(id, url));
            var target = new BrowserTarget("role", "treeitem", Name: "Asset 2");
            for (var i = 0; i < 260; i++) Assert.Null((await browser.ExecuteAsync(new(id, new BrowserFind(target)))).ErrorCode);
            Assert.Equal("provider_unavailable", (await browser.ExecuteAsync(new(other, new BrowserClick(target)))).ErrorCode);
            await browser.ExecuteAsync(BrowserTestRequests.Navigate(other, url));
            Assert.Null((await browser.ExecuteAsync(new(other, new BrowserClick(target)))).ErrorCode);
            Assert.Equal("Nothing selected", await browser.ContextFor(id)!.Pages[0].GetByRole(AriaRole.Status).InnerTextAsync());
            var page = browser.ContextFor(id)!.Pages[0];
            await page.GetByRole(AriaRole.Treeitem, new() { Name = "Asset 2", Exact = true }).EvaluateAsync("el=>el.remove()");
            Assert.Equal("target_missing", (await browser.ExecuteAsync(new(id, new BrowserClick(target)))).ErrorCode);
            var unique = new BrowserTarget("role", "treeitem", Name: "Asset 1");
            await page.GetByRole(AriaRole.Treeitem, new() { Name = "Asset 1", Exact = true }).EvaluateAsync("el=>el.after(el.cloneNode(true))");
            Assert.Equal("ambiguous_target", (await browser.ExecuteAsync(new(id, new BrowserClick(unique)))).ErrorCode);
            await browser.ExecuteAsync(BrowserTestRequests.Navigate(id, url));
            Assert.Null((await browser.ExecuteAsync(new(id, new BrowserClick(unique)))).ErrorCode);
            Assert.Equal("Selected Asset 1", await page.GetByRole(AriaRole.Status).InnerTextAsync());
        }
        finally { await browser.StopAsync(default); }
    }

    [Fact]
    public async Task Dense_spa_find_is_independent_of_snapshot_and_survives_rerender_and_scoped_reads()
    {
        var browser = Create(); await browser.StartAsync(CancellationToken.None); var id = Guid.NewGuid();
        try
        {
            var clock = Stopwatch.StartNew();
            var opened = await browser.ExecuteAsync(BrowserTestRequests.Navigate(id, new(browser.HostPolicy.NavigationOrigins.Single() + "/browser-dense.html")));
            output.WriteLine($"DENSE navigate_observe_ms={clock.Elapsed.TotalMilliseconds:F1}");
            Assert.Null(opened.ErrorCode); Assert.Empty(opened.Observation!.Targets);
            Assert.True(opened.Observation.ContentTruncated); Assert.DoesNotContain("Entry 1999", opened.Observation.Content);
            // Make any snapshot attempt fail: find must still succeed directly through the live Locator.
            browser.CaptureProbe = () => new InvalidOperationException("find must not snapshot");
            clock.Restart();
            var last = await Find(browser, id, new BrowserTarget("role", "treeitem", Name: "Entry 1999"));
            output.WriteLine($"DENSE find_ms={clock.Elapsed.TotalMilliseconds:F1}");
            browser.CaptureProbe = null;
            var page = browser.ContextFor(id)!.Pages[0];
            await page.GetByRole(AriaRole.Tree, new() { Name = "Catalog" }).EvaluateAsync("el=>render()");
            clock.Restart();
            Assert.Null((await browser.ExecuteAsync(new(id, new BrowserClick(Target: last)))).ErrorCode);
            output.WriteLine($"DENSE click_observe_ms={clock.Elapsed.TotalMilliseconds:F1}");
            Assert.Equal("Opened Entry 1999", await page.GetByRole(AriaRole.Status).InnerTextAsync());
            var queue = await Find(browser, id, new BrowserTarget("role", "grid", Name: "Review queue"));
            var scoped = await browser.ExecuteAsync(new(id, new BrowserObserve(Target: queue, Depth: 2)));
            Assert.Null(scoped.ErrorCode); Assert.True(scoped.Observation!.Content!.Length < opened.Observation.Content!.Length / 10);
            Assert.DoesNotContain("Entry 1999", scoped.Observation.Content);
            var review = await Find(browser, id, new BrowserTarget("role", "button", Name: "Open review", Within: BrowserTestQueries.Scope(queue)));
            Assert.Null((await browser.ExecuteAsync(new(id, new BrowserClick(Target: review)))).ErrorCode);
            Assert.Equal("Review entry opened", await page.GetByRole(AriaRole.Status).InnerTextAsync());
            Assert.Equal("closed", (await browser.ExecuteAsync(BrowserTestRequests.Close(id))).Status);
            Assert.Null(browser.ContextFor(id));
            Assert.Equal("already_closed", (await browser.ExecuteAsync(BrowserTestRequests.Close(id))).Status);
            Assert.Null((await browser.ExecuteAsync(BrowserTestRequests.Navigate(id, new(browser.HostPolicy.NavigationOrigins.Single() + "/")))).ErrorCode);
        }
        finally { await browser.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Shallow_inspection_never_grants_ordinal_authority_for_existing_duplicates()
    {
        var browser = Create(); await browser.StartAsync(CancellationToken.None); var id = Guid.NewGuid();
        try
        {
            await browser.ExecuteAsync(BrowserTestRequests.Navigate(id, new(browser.HostPolicy.NavigationOrigins.Single() + "/browser-native.html?compact=1")));
            var page = browser.ContextFor(id)!.Pages[0];
            await page.GetByRole(AriaRole.Tree).EvaluateAsync("el=>{el.innerHTML='<button>Choose</button><div role=group aria-label=Nested><button onclick=\"this.textContent=String(1)\">Choose</button></div>'}");
            var root = await Find(browser, id, new BrowserTarget("role", "tree", Name: "Assets"));
            Assert.Null((await browser.ExecuteAsync(new(id, new BrowserObserve(Target: root, Depth: 2)))).ErrorCode);
            Assert.Equal("ambiguous_target", (await browser.ExecuteAsync(new(id, new BrowserFind(Target: new BrowserTarget("role", "button", Name: "Choose", Within: BrowserTestQueries.Scope(root)))))).ErrorCode);
            var group = await Find(browser, id, new BrowserTarget("role", "group", Name: "Nested", Within: BrowserTestQueries.Scope(root)));
            var chosen = await Find(browser, id, new BrowserTarget("role", "button", Name: "Choose", Within: BrowserTestQueries.Scope(group)));
            Assert.Null((await browser.ExecuteAsync(new(id, new BrowserClick(Target: chosen)))).ErrorCode);
            Assert.Equal("1", await page.GetByRole(AriaRole.Group, new() { Name = "Nested" }).GetByRole(AriaRole.Button).InnerTextAsync());
        }
        finally { await browser.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Virtualized_content_open_shadow_and_missing_target_wait_use_native_semantics()
    {
        var browser = Create(); await browser.StartAsync(default); var id = Guid.NewGuid();
        try
        {
            await browser.ExecuteAsync(BrowserTestRequests.Navigate(id, new(browser.HostPolicy.NavigationOrigins.Single() + "/")));
            var page = browser.ContextFor(id)!.Pages[0];
            await page.SetContentAsync("""
                <div role="region" aria-label="Records" style="height:150px;overflow:auto;width:400px">
                  <button>Row 1</button><div style="height:2000px"></div>
                </div><output role="status">Ready</output><div id="open"></div><div id="closed"></div>
                <script>
                  const panel=document.querySelector('[role=region]');
                  panel.onscroll=()=>{panel.querySelector('button').textContent='Row 200';};
                  const open=document.getElementById('open').attachShadow({mode:'open'});
                  open.innerHTML='<button>Shadow action</button>';
                  open.querySelector('button').onclick=()=>document.querySelector('output').textContent='Shadow changed';
                  document.getElementById('closed').attachShadow({mode:'closed'}).innerHTML='<button>Closed action</button>';
                </script>
                """);
            async Task<BrowserResult> Query(string name) => await browser.ExecuteAsync(new(id, new BrowserFind(Target: new BrowserTarget("role", "button", Name: name))));
            Assert.Equal("not_found", (await Query("Row 200")).ErrorCode);
            var region = await Find(browser, id, new BrowserTarget("role", "region", Name: "Records"));
            Assert.Null((await browser.ExecuteAsync(new(id, new BrowserScroll(Target: region, DeltaY: 400)))).ErrorCode);
            Assert.Null((await Query("Row 200")).ErrorCode);
            var shadow = await Find(browser, id, new BrowserTarget("role", "button", Name: "Shadow action"));
            Assert.Null((await browser.ExecuteAsync(new(id, new BrowserClick(Target: shadow)))).ErrorCode);
            Assert.Equal("Shadow changed", await page.GetByRole(AriaRole.Status).InnerTextAsync());
            Assert.Equal("not_found", (await Query("Closed action")).ErrorCode);
            await page.GetByRole(AriaRole.Button, new() { Name = "Shadow action" }).EvaluateAsync("el=>el.remove()");
            Assert.Null((await browser.ExecuteAsync(new(id, new BrowserWaitFor(Target: shadow, Condition: "target", State: "detached", TimeoutMs: 500)))).ErrorCode);
        }
        finally { await browser.StopAsync(default); }
    }

    internal static async Task<BrowserTarget> Find(NativePlaywrightBrowser browser, Guid id, BrowserTarget query)
    {
        var result = await browser.ExecuteAsync(new(id, new BrowserFind(Target: query)));
        Assert.Null(result.ErrorCode);
        using var doc = JsonDocument.Parse(result.DataJson!);
        return Assert.Single(doc.RootElement.GetProperty("matches").EnumerateArray()).GetProperty("target").Deserialize<BrowserTarget>(JsonSerializerOptions.Web)!;
    }

    internal static NativePlaywrightBrowser Create() => new(new BrowserOptions { Enabled = true, Headless = true, FixtureEnabled = true,
        FixturePort = 0, InteractionMode = "InteractiveDemo", NavigationOrigins = ["http://127.0.0.1:5091"], InteractionOrigins = ["http://127.0.0.1:5091"] }, loggerFactory: null);
}
