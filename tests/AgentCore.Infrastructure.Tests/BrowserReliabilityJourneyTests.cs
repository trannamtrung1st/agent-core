using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Browser;
using Microsoft.Playwright;

namespace AgentCore.Infrastructure.Tests;

[Collection(BrowserChromiumCollection.Name)]
public sealed class BrowserReliabilityJourneyTests
{
    [Fact]
    public async Task Malformed_unknown_foreign_invalidated_and_detached_targets_are_distinct()
    {
        var browser = Create(); await browser.StartAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        try
        {
            var initial = await browser.NavigateAsync(new(id, new Uri(browser.HostPolicy.NavigationOrigins.Single() + "/browser-v2.html?compact=1")));
            Assert.Null(initial.ErrorCode);
            async Task<BrowserCommandResult> Click(string reference, Guid? session = null) => await browser.ExecuteAsync(new(session ?? id, "browser.click", JsonSerializer.SerializeToElement(new { @ref = reference, clickCount = 1 })));
            Assert.Equal("invalid_reference", (await Click("main")).ErrorCode);
            Assert.Equal("unknown_reference", (await Click("el_0000000000000000000000")).ErrorCode);
            var reference = initial.Observation!.Elements.Single(e => e.Name == "Asset 2").Ref;
            var other = Guid.NewGuid();
            await browser.NavigateAsync(new(other, new Uri(browser.HostPolicy.NavigationOrigins.Single() + "/browser-v2.html?compact=1")));
            Assert.Equal("wrong_session_reference", (await Click(reference, other)).ErrorCode);
            var page = browser.ContextFor(id)!.Pages[0];
            await page.GetByRole(AriaRole.Treeitem, new() { Name = "Asset 2", Exact = true }).EvaluateAsync("el=>el.remove()");
            Assert.Equal("target_missing", (await Click(reference)).ErrorCode);
            var unique = initial.Observation.Elements.Single(e => e.Name == "Asset 1").Ref;
            await page.GetByRole(AriaRole.Treeitem, new() { Name = "Asset 1", Exact = true }).EvaluateAsync("el=>el.after(el.cloneNode(true))");
            Assert.Equal("ambiguous_reference", (await Click(unique)).ErrorCode);
            await page.GetByRole(AriaRole.Treeitem, new() { Name = "Asset 1", Exact = true }).Last.EvaluateAsync("el=>el.remove()");
            var heading = initial.Observation.Elements.Single(e => e.Role == "heading").Ref;
            Assert.Equal("non_actionable_target", (await Click(heading)).ErrorCode);
            var fresh = await browser.SnapshotAsync(id);
            var valid = fresh.Observation!.Elements.Single(e => e.Name == "Asset 1").Ref;
            await browser.SnapshotAsync(id);
            Assert.Equal("stale_reference", (await Click(valid)).ErrorCode);
        }
        finally { await browser.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Dense_nested_spa_discovery_scoping_pagination_rerender_and_closure()
    {
        var browser = Create(); await browser.StartAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        try
        {
            var opened = await browser.NavigateAsync(new(id, new Uri(browser.HostPolicy.NavigationOrigins.Single() + "/browser-dense.html")));
            Assert.Null(opened.ErrorCode);
            var full = opened.Observation!;
            Assert.InRange(full.Elements.Count, 2000, 4096);
            Assert.True(full.ContentTruncated);
            Assert.DoesNotContain("Entry 1999", full.Content);
            async Task<BrowserCommandResult> Run(string tool, object args) => await browser.ExecuteAsync(new(id, tool, JsonSerializer.SerializeToElement(args)));
            async Task<string> Find(object args)
            {
                var found = await Run("browser.find", args); Assert.Null(found.ErrorCode);
                using var json = JsonDocument.Parse(found.DataJson!);
                return Assert.Single(json.RootElement.GetProperty("matches").EnumerateArray()).GetProperty("ref").GetString()!;
            }
            var last = await Find(new { text = "Entry 1999", role = "treeitem", ancestor = "Collection 19" });
            var page = browser.ContextFor(id)!.Pages[0];
            // Replace all nodes like a React rerender, preserving semantics but no element handles.
            await page.GetByRole(AriaRole.Tree, new() { Name = "Catalog" }).EvaluateAsync("el=>render()");
            var click = await Run("browser.click", new { @ref = last }); Assert.Null(click.ErrorCode);
            Assert.Equal("Opened Entry 1999", await page.GetByRole(AriaRole.Status).InnerTextAsync());
            Assert.Equal("stale_reference", (await Run("browser.click", new { @ref = last })).ErrorCode);
            var paged = await Run("browser.find", new { text = "Entry", role = "treeitem", limit = 3, offset = 1990 });
            using (var json = JsonDocument.Parse(paged.DataJson!))
            {
                Assert.Equal(2000, json.RootElement.GetProperty("matchCount").GetInt32());
                Assert.Equal(3, json.RootElement.GetProperty("returnedCount").GetInt32());
                Assert.Equal(1993, json.RootElement.GetProperty("nextOffset").GetInt32());
            }
            var review = await Find(new { text = "Open review", role = "button", ancestor = "Review queue" });
            Assert.Null((await Run("browser.click", new { @ref = review })).ErrorCode);
            Assert.Equal("Review entry opened", await page.GetByRole(AriaRole.Status).InnerTextAsync());
            for (var i = 0; i < 2; i++)
            {
                var region = await Find(new { text = "Review queue", role = "grid" });
                var scoped = await Run("browser.snapshot", new { targetRef = region, depth = 2 }); Assert.Null(scoped.ErrorCode);
                Assert.True(scoped.Snapshot!.CapturedNodeCount < full.CapturedNodeCount / 100);
                Assert.True(scoped.Snapshot.Content!.Length < full.Content!.Length / 10);
                Assert.DoesNotContain("Entry 1999", scoped.Snapshot.VisibleText);
                Assert.NotEmpty(await Find(new { text = "Entry 1999", role = "treeitem" }));
            }
            var queue = await Find(new { text = "Review queue", role = "grid" });
            var targeted = await Run("browser.find", new { text = "Open review", role = "button", targetRef = queue });
            using (var json = JsonDocument.Parse(targeted.DataJson!)) Assert.Single(json.RootElement.GetProperty("matches").EnumerateArray());
            for (var i = 0; i < 2; i++)
            {
                var collection = await Find(new { text = "Collection 19", name = "Collection 19", role = "treeitem", ancestor = "Catalog" });
                var limited = await Run("browser.snapshot", new { targetRef = collection, depth = 1 });
                Assert.Null(limited.ErrorCode);
                Assert.DoesNotContain("Entry 1999", limited.Snapshot!.VisibleText);
                Assert.DoesNotContain("Entry 1999", limited.Snapshot.Content);
                Assert.NotEmpty(await Find(new { text = "Entry 1999", role = "treeitem", ancestor = "Catalog" }));
                collection = await Find(new { text = "Collection 19", name = "Collection 19", role = "treeitem", ancestor = "Catalog" });
                Assert.Null((await Run("browser.snapshot", new { targetRef = collection, depth = 3 })).ErrorCode);
                var catalog = await Find(new { text = "Catalog", role = "tree" });
                Assert.NotEmpty(await Find(new { text = "Entry 1999", role = "treeitem", ancestor = "Catalog", targetRef = catalog }));
                var entries = await Find(new { text = "Entries 19", role = "group", ancestor = "Collection 19" });
                Assert.Null((await Run("browser.snapshot", new { targetRef = entries, depth = 2 })).ErrorCode);
                Assert.NotEmpty(await Find(new { text = "Entry 1999", role = "treeitem", ancestor = "Catalog" }));
            }
            var refreshedCollection = await Find(new { text = "Collection 19", name = "Collection 19", role = "treeitem", ancestor = "Catalog" });
            var collectionLocator = page.GetByRole(AriaRole.Treeitem, new() { Name = "Collection 19", Exact = true });
            await collectionLocator.EvaluateAsync("el=>el.after(el.cloneNode(false))");
            Assert.Equal("ambiguous_reference", (await Run("browser.click", new { @ref = refreshedCollection })).ErrorCode);
            await collectionLocator.Last.EvaluateAsync("el=>el.remove()");
            var shallow = await Run("browser.snapshot", new { depth = 1 }); Assert.Null(shallow.ErrorCode);
            Assert.True(shallow.Snapshot!.Elements.Count < full.Elements.Count / 100);
            Assert.NotEmpty(await Find(new { text = "Entry 1999", role = "treeitem" }));
            Assert.Equal("closed", (await browser.CloseAsync(id)).Status);
            Assert.Null(browser.ContextFor(id));
            Assert.Equal("already_closed", (await browser.CloseAsync(id)).Status);
        }
        finally { await browser.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Depth_omitted_existing_duplicate_is_not_new_ambiguity()
    {
        var browser = Create(); await browser.StartAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        try
        {
            await browser.NavigateAsync(new(id, new Uri(browser.HostPolicy.NavigationOrigins.Single() + "/browser-v2.html?compact=1")));
            var page = browser.ContextFor(id)!.Pages[0];
            await page.GetByRole(AriaRole.Tree, new() { Name = "Assets", Exact = true }).EvaluateAsync("el=>{el.innerHTML='<button aria-label=Choose onclick=\"this.textContent=String(1)\">Choose</button><div role=group aria-label=Nested><button aria-label=Choose>Choose</button></div>'}");
            var full = await browser.SnapshotAsync(id);
            var root = full.Observation!.Elements.Single(e => e.Role == "tree").Ref;
            var scoped = await browser.ExecuteAsync(new(id, "browser.snapshot", JsonSerializer.SerializeToElement(new { targetRef = root, depth = 2 })));
            Assert.Null(scoped.ErrorCode);
            var button = Assert.Single(scoped.Snapshot!.Elements, e => e.Name == "Choose");
            var clicked = await browser.ExecuteAsync(new(id, "browser.click", JsonSerializer.SerializeToElement(new { @ref = button.Ref })));
            Assert.Null(clicked.ErrorCode);
            Assert.Equal("1", await page.GetByRole(AriaRole.Button, new() { Name = "Choose", Exact = true }).First.InnerTextAsync());
        }
        finally { await browser.StopAsync(CancellationToken.None); }
    }

    internal static PlaywrightBrowser Create() => new(new BrowserOptions { Enabled = true, Headless = true, FixtureEnabled = true,
        FixturePort = 0, InteractionMode = "InteractiveDemo", NavigationOrigins = ["http://127.0.0.1:5091"], InteractionOrigins = ["http://127.0.0.1:5091"] }, loggerFactory: null);
}
