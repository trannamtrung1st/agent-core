using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Browser;
using Microsoft.Playwright;

namespace AgentCore.Infrastructure.Tests;

[Collection(BrowserChromiumCollection.Name)]
public sealed class AdaptiveBrowserEvidenceTests
{
    [Theory]
    [InlineData("resize")]
    [InlineData("navigate")]
    [InlineData("tab")]
    [InlineData("layout")]
    [InlineData("scroll")]
    [InlineData("action")]
    public async Task Changed_state_rejects_old_pixels_then_fresh_capture_recovers(string change)
    {
        var browser = NewBrowser(); await browser.StartAsync(default); var id = Guid.NewGuid();
        try
        {
            var url = browser.HostPolicy.NavigationOrigins.Single() + "/adaptive-browser.html?mode=weak";
            Assert.Null((await browser.ExecuteAsync(new(id, new BrowserNavigate(url)))).ErrorCode);
            var shot = await browser.ExecuteAsync(new(id, new BrowserScreenshot())); Assert.Null(shot.ErrorCode);
            Assert.True(JsonSerializer.Deserialize<JsonElement>(shot.DataJson!).GetProperty("coordinateEvidence").GetBoolean());
            // Ordinary semantic refresh does not consume still-current visual evidence.
            Assert.Null((await browser.ExecuteAsync(new(id, new BrowserObserve()))).ErrorCode);
            var page = browser.ContextFor(id)!.Pages.Single();
            switch (change)
            {
                case "resize": await browser.ExecuteAsync(new(id, new BrowserResize(1000, 700))); break;
                case "navigate": await browser.ExecuteAsync(new(id, new BrowserNavigate(url + "&new=1"))); break;
                case "tab": await browser.ExecuteAsync(new(id, new BrowserTabs("new", Url: url))); break;
                case "layout": await page.EvaluateAsync("() => document.querySelector('.weak').style.left='600px'"); break;
                case "scroll": await page.EvaluateAsync("() => { document.body.style.height='1600px'; scrollTo(0,100); }"); break;
                case "action": await browser.ExecuteAsync(new(id, new BrowserClick(new("role", "button", Name: "Cooling project")))); break;
            }
            var stale = await browser.ExecuteAsync(new(id, new BrowserMouse("click", 680, 165, SnapshotId: shot.Observation!.SnapshotId)));
            Assert.Equal("stale_visual_evidence", stale.ErrorCode); Assert.False(stale.EffectAttempted);
            page = browser.ContextFor(id)!.Pages.Last();
            Assert.DoesNotContain("Selected", await page.GetByRole(AriaRole.Status).InnerTextAsync());
            if (change == "scroll") await page.EvaluateAsync("() => scrollTo(0,0)");
            var refreshed = await browser.ExecuteAsync(new(id, new BrowserScreenshot())); Assert.Null(refreshed.ErrorCode);
            var box = (await page.Locator(".weak:not(.wrong)").BoundingBoxAsync())!;
            var selected = await browser.ExecuteAsync(new(id, new BrowserMouse("click", box.X + box.Width / 2, box.Y + box.Height / 2, SnapshotId: refreshed.Observation!.SnapshotId)));
            Assert.Null(selected.ErrorCode);
            Assert.Equal("Selected PUMP-1042", await page.GetByRole(AriaRole.Status).InnerTextAsync());
            Assert.True((await browser.ExecuteAsync(new(id, new BrowserVerify("text", new("role", "status"), "Selected PUMP-1042")))).ApplicationOutcomeVerified);
            Assert.Equal("stale_visual_evidence", (await browser.ExecuteAsync(new(id, new BrowserMouse("click", box.X, box.Y, SnapshotId: refreshed.Observation!.SnapshotId)))).ErrorCode);
        }
        finally { await browser.StopAsync(default); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Context_images_and_foreign_session_ids_never_authorize_viewport_actions(bool fullPage)
    {
        var browser = NewBrowser(); await browser.StartAsync(default); var id = Guid.NewGuid(); var other = Guid.NewGuid();
        try
        {
            var url = browser.HostPolicy.NavigationOrigins.Single() + "/adaptive-browser.html?mode=weak";
            await browser.ExecuteAsync(new(id, new BrowserNavigate(url))); await browser.ExecuteAsync(new(other, new BrowserNavigate(url)));
            var viewport = await browser.ExecuteAsync(new(id, new BrowserScreenshot()));
            var semantic = await browser.ExecuteAsync(new(id, new BrowserObserve()));
            Assert.Equal("stale_visual_evidence", (await browser.ExecuteAsync(new(id, new BrowserMouse("click", 680, 165, SnapshotId: semantic.Observation!.SnapshotId)))).ErrorCode);
            Assert.Equal("stale_visual_evidence", (await browser.ExecuteAsync(new(other, new BrowserMouse("click", 680, 165, SnapshotId: viewport.Observation!.SnapshotId)))).ErrorCode);
            var context = await browser.ExecuteAsync(new(id, new BrowserScreenshot(FullPage: fullPage, Target: fullPage ? null : new("role", "main"))));
            Assert.Null(context.ErrorCode);
            Assert.False(JsonSerializer.Deserialize<JsonElement>(context.DataJson!).GetProperty("coordinateEvidence").GetBoolean());
            Assert.Equal("stale_visual_evidence", (await browser.ExecuteAsync(new(id, new BrowserMouse("click", 680, 165, SnapshotId: context.Observation!.SnapshotId)))).ErrorCode);
            Assert.Equal("stale_visual_evidence", (await browser.ExecuteAsync(new(id, new BrowserMouse("click", 680, 165, SnapshotId: viewport.Observation!.SnapshotId)))).ErrorCode);
        }
        finally { await browser.StopAsync(default); }
    }

    [Fact]
    public async Task Changes_during_combined_observation_withhold_coordinate_evidence()
    {
        var browser = NewBrowser(); await browser.StartAsync(default); var id = Guid.NewGuid();
        try
        {
            var url = browser.HostPolicy.NavigationOrigins.Single() + "/adaptive-browser.html?mode=weak";
            Assert.Null((await browser.ExecuteAsync(new(id, new BrowserNavigate(url)))).ErrorCode);
            var page = browser.ContextFor(id)!.Pages.Single();
            browser.CaptureProbe = () =>
            {
                browser.CaptureProbe = null;
                page.EvaluateAsync("() => document.querySelector('.weak').style.left='600px'").GetAwaiter().GetResult();
                return null;
            };
            var changed = await browser.ExecuteAsync(new(id, new BrowserScreenshot()));
            Assert.Null(changed.ErrorCode); Assert.NotEmpty(changed.Bytes!);
            Assert.False(changed.Observation!.Settled);
            Assert.False(JsonSerializer.Deserialize<JsonElement>(changed.DataJson!).GetProperty("coordinateEvidence").GetBoolean());
            var refused = await browser.ExecuteAsync(new(id, new BrowserMouse("click", 680, 165, SnapshotId: changed.Observation!.SnapshotId)));
            Assert.Equal("stale_visual_evidence", refused.ErrorCode); Assert.False(refused.EffectAttempted);
            var fresh = await browser.ExecuteAsync(new(id, new BrowserScreenshot()));
            Assert.Null(fresh.ErrorCode);
            Assert.True(JsonSerializer.Deserialize<JsonElement>(fresh.DataJson!).GetProperty("coordinateEvidence").GetBoolean());
        }
        finally { browser.CaptureProbe = null; await browser.StopAsync(default); }
    }

    private static NativePlaywrightBrowser NewBrowser() => new(new BrowserOptions { Enabled = true, Headless = true, FixtureEnabled = true,
        FixturePort = 0, InteractionMode = "InteractiveDemo", NavigationOrigins = ["http://127.0.0.1:5091"], InteractionOrigins = ["http://127.0.0.1:5091"] }, loggerFactory: null);
}
