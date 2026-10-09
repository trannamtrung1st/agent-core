using AgentCore.Application.Ports;
using AgentCore.Tests.Shared;
using Microsoft.Playwright;
namespace AgentCore.Infrastructure.Tests;

[Collection(BrowserChromiumCollection.Name)]
public sealed class BrowserCustomTargetJourneyTests
{
    [Fact]
    public async Task Duplicate_delegated_captions_require_unique_native_region_scope()
    {
        var browser = BrowserReliabilityJourneyTests.Create(); await browser.StartAsync(CancellationToken.None); var id = Guid.NewGuid();
        try
        {
            await browser.ExecuteAsync(BrowserTestRequests.Navigate(id, new(browser.HostPolicy.NavigationOrigins.Single() + "/browser-custom-tree.html")));
            var page = browser.ContextFor(id)!.Pages[0];
            await page.GetByRole(AriaRole.Tree).EvaluateAsync("el=>{el.innerHTML='<span>Choose</span><span style=display:none>Choose</span><div role=group aria-label=North><span class=caption>Choose</span></div><div role=group aria-label=South><span class=caption>Choose</span></div>';el.addEventListener('click',e=>{document.querySelector('[role=status]').textContent=e.target.parentElement.getAttribute('aria-label')})}");
            Assert.Equal("ambiguous_target", (await browser.ExecuteAsync(new(id, new BrowserFind(Target: new BrowserTarget("text", "Choose"))))).ErrorCode);
            var south = await BrowserReliabilityJourneyTests.Find(browser, id, new BrowserTarget("role", "group", Name: "South"));
            var caption = await BrowserReliabilityJourneyTests.Find(browser, id, new BrowserTarget("text", "Choose", Within: BrowserTestQueries.Scope(south)));
            await browser.ExecuteAsync(BrowserTestRequests.Inspect(id));
            Assert.Null((await browser.ExecuteAsync(new(id, new BrowserClick(Target: caption)))).ErrorCode);
            Assert.Equal("South", await page.GetByRole(AriaRole.Status).InnerTextAsync());
        }
        finally { await browser.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Deep_roleless_caption_rerenders_and_protected_reclassification_blocks_effects()
    {
        var browser = BrowserReliabilityJourneyTests.Create(); await browser.StartAsync(CancellationToken.None); var id = Guid.NewGuid();
        try
        {
            var nav = await browser.ExecuteAsync(BrowserTestRequests.Navigate(id, new(browser.HostPolicy.NavigationOrigins.Single() + "/browser-custom-tree.html")));
            Assert.DoesNotContain("custom-private-token", nav.Observation!.Content);
            var page = browser.ContextFor(id)!.Pages[0];
            var target = await BrowserReliabilityJourneyTests.Find(browser, id, new BrowserTarget("text", "Entry 1999"));
            await page.GetByRole(AriaRole.Tree).EvaluateAsync("el=>render()");
            Assert.Null((await browser.ExecuteAsync(new(id, new BrowserClick(Target: target)))).ErrorCode);
            Assert.Equal("Entry 1999 detail", await page.GetByRole(AriaRole.Heading, new() { Level = 2 }).InnerTextAsync());
            var caption = page.GetByText("Entry 1998", new() { Exact = true });
            target = await BrowserReliabilityJourneyTests.Find(browser, id, new BrowserTarget("text", "Entry 1998"));
            await caption.EvaluateAsync("el=>el.after(el.cloneNode(true))");
            Assert.Equal("ambiguous_target", (await browser.ExecuteAsync(new(id, new BrowserClick(Target: target)))).ErrorCode);
            await caption.Last.EvaluateAsync("el=>el.remove()");
            await caption.EvaluateAsync("el=>el.setAttribute('aria-label','API key')");
            Assert.Equal("forbidden", (await browser.ExecuteAsync(new(id, new BrowserClick(Target: target)))).ErrorCode);
            Assert.Equal("Opened Entry 1999", await page.GetByRole(AriaRole.Status).InnerTextAsync());
        }
        finally { await browser.StopAsync(CancellationToken.None); }
    }
}
