using System.Text.Json;
using AgentCore.Application.Ports;
using Microsoft.Playwright;

namespace AgentCore.Infrastructure.Tests;

[Collection(BrowserChromiumCollection.Name)]
public sealed class BrowserCustomTargetJourneyTests
{
    [Fact]
    public async Task Duplicate_custom_captions_keep_their_region_and_native_text_ordinal()
    {
        var browser = BrowserReliabilityJourneyTests.Create(); await browser.StartAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        try
        {
            await browser.NavigateAsync(new(id, new Uri(browser.HostPolicy.NavigationOrigins.Single() + "/browser-custom-tree.html")));
            var page = browser.ContextFor(id)!.Pages[0];
            await page.GetByRole(AriaRole.Tree).EvaluateAsync("el=>{el.innerHTML='<span>Choose</span><span style=display:none>Choose</span><div role=group aria-label=North><span class=caption>Choose</span></div><div role=group aria-label=South><span class=caption>Choose</span></div>';el.removeAttribute('aria-label');document.querySelector('h1').id='tree-label';el.setAttribute('aria-labelledby','tree-label');el.addEventListener('click',e=>{document.querySelector('[role=status]').textContent=e.target.parentElement.getAttribute('aria-label')})}");
            var observed = await browser.SnapshotAsync(id);
            var tree = observed.Observation!.Elements.Single(e => e.Role == "tree").Ref;
            async Task<string> Find(string ancestor)
            {
                var result = await browser.ExecuteAsync(new(id, "browser.find", JsonSerializer.SerializeToElement(new { text = "Choose", ancestor, targetRef = tree })));
                Assert.Null(result.ErrorCode);
                using var json = JsonDocument.Parse(result.DataJson!);
                return Assert.Single(json.RootElement.GetProperty("matches").EnumerateArray()).GetProperty("ref").GetString()!;
            }
            var south = await Find("South");
            Assert.NotEqual(south, await Find("North"));
            Assert.Null((await browser.ExecuteAsync(new(id, "browser.click", JsonSerializer.SerializeToElement(new { @ref = south })))).ErrorCode);
            Assert.Equal("South", await page.GetByRole(AriaRole.Status).InnerTextAsync());
        }
        finally { await browser.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Delegated_custom_caption_is_discovered_scoped_rebound_clicked_and_verified()
    {
        var browser = BrowserReliabilityJourneyTests.Create(); await browser.StartAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        try
        {
            var opened = await browser.NavigateAsync(new(id, new Uri(browser.HostPolicy.NavigationOrigins.Single() + "/browser-custom-tree.html")));
            Assert.Null(opened.ErrorCode);
            var page = browser.ContextFor(id)!.Pages[0];
            Assert.Equal("No detail opened", await page.GetByRole(AriaRole.Heading, new() { Level = 2 }).InnerTextAsync());
            async Task<BrowserCommandResult> Run(string tool, object args) => await browser.ExecuteAsync(new(id, tool, JsonSerializer.SerializeToElement(args)));
            async Task<string> Find(object args)
            {
                var found = await Run("browser.find", args); Assert.Null(found.ErrorCode);
                using var json = JsonDocument.Parse(found.DataJson!);
                var match = Assert.Single(json.RootElement.GetProperty("matches").EnumerateArray());
                Assert.Contains("click", match.GetProperty("actions").EnumerateArray().Select(a => a.GetString()));
                return match.GetProperty("ref").GetString()!;
            }
            var target = await Find(new { text = "Entry 1999", name = "Entry 1999", ancestor = "Assets" });
            Assert.DoesNotContain("custom-private-token", opened.Observation!.Content);
            Assert.DoesNotContain(opened.Observation.Elements, e => e.Role == "generic" && (e.Name is "Static caption" or "Hidden entry" or "Protected caption"));
            var assets = opened.Observation.Elements.Single(e => e.Role == "tree").Ref;
            Assert.Null((await Run("browser.snapshot", new { targetRef = assets, depth = 4 })).ErrorCode);
            Assert.Equal("stale_reference", (await Run("browser.click", new { @ref = target })).ErrorCode);
            using (var found = JsonDocument.Parse((await Run("browser.find", new { text = "Assets", role = "tree" })).DataJson!))
                assets = found.RootElement.GetProperty("matches")[0].GetProperty("ref").GetString()!;
            target = await Find(new { text = "Entry 1999", role = "generic", targetRef = assets });
            await page.GetByRole(AriaRole.Tree).EvaluateAsync("el=>render()");
            Assert.Null((await Run("browser.click", new { @ref = target })).ErrorCode);
            Assert.Equal("Entry 1999 detail", await page.GetByRole(AriaRole.Heading, new() { Level = 2 }).InnerTextAsync());
            Assert.Equal("Opened Entry 1999", await page.GetByRole(AriaRole.Status).InnerTextAsync());
            target = await Find(new { text = "Entry 1998", role = "generic" });
            var caption = page.GetByText("Entry 1998", new() { Exact = true });
            await caption.EvaluateAsync("el=>el.after(el.cloneNode(true))");
            Assert.Equal("ambiguous_reference", (await Run("browser.click", new { @ref = target })).ErrorCode);
            await caption.Last.EvaluateAsync("el=>el.remove()");
            await caption.EvaluateAsync("el=>el.style.cursor='default'");
            Assert.Equal("non_actionable_target", (await Run("browser.click", new { @ref = target })).ErrorCode);
            await caption.EvaluateAsync("el=>{el.className='caption';el.style.cursor='pointer';el.setAttribute('aria-label','API key')}");
            Assert.NotNull((await Run("browser.click", new { @ref = target })).ErrorCode);
            Assert.Equal("Opened Entry 1999", await page.GetByRole(AriaRole.Status).InnerTextAsync());
            await caption.EvaluateAsync("el=>{el.removeAttribute('aria-label');el.style.cursor='default'}");
            Assert.Null((await Run("browser.snapshot", new { targetRef = target, depth = 1 })).ErrorCode);
        }
        finally { await browser.StopAsync(CancellationToken.None); }
    }
}
