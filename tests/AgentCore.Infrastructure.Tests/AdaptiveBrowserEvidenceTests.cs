using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Browser;
using Microsoft.Playwright;

namespace AgentCore.Infrastructure.Tests;

[Collection(BrowserChromiumCollection.Name)]
public sealed class AdaptiveBrowserEvidenceTests(Xunit.Abstractions.ITestOutputHelper output)
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Semantic_failure_preserves_masked_pixels_and_independent_coordinate_authority(bool privacyFailure)
    {
        var browser = NewBrowser(); await browser.StartAsync(default); var id = Guid.NewGuid();
        try
        {
            await browser.ExecuteAsync(new(id, new BrowserNavigate(browser.HostPolicy.NavigationOrigins.Single() + "/adaptive-browser.html?mode=weak")));
            var page = browser.ContextFor(id)!.Pages.Single();
            await page.EvaluateAsync("() => { const e=document.createElement('div'); e.dataset.sensitive=''; e.textContent='private'; Object.assign(e.style,{position:'absolute',left:'0px',top:'0px',width:'100px',height:'30px',background:'red'}); document.body.append(e); }");
            browser.CaptureProbe = () =>
            {
                if (privacyFailure) page.EvaluateAsync("() => { const query=document.querySelectorAll.bind(document); document.querySelectorAll=s=>{if(s.includes('contenteditable=true')) throw new Error('privacy unavailable');return query(s)}; }").GetAwaiter().GetResult();
                return new TimeoutException("semantic unavailable");
            };
            var shot = await browser.ExecuteAsync(new(id, new BrowserScreenshot()));
            if (privacyFailure) { Assert.NotNull(shot.ErrorCode); Assert.Null(shot.Bytes); return; }
            Assert.Null(shot.ErrorCode); Assert.NotEmpty(shot.Bytes!);
            Assert.True(shot.Observation!.ObservationUnavailable); Assert.Empty(shot.Observation.Content);
            Assert.True(JsonSerializer.Deserialize<JsonElement>(shot.DataJson!).GetProperty("coordinateEvidence").GetBoolean());
            using var pixels = SkiaSharp.SKBitmap.Decode(shot.Bytes);
            Assert.Equal(new SkiaSharp.SKColor(17, 17, 17), pixels.GetPixel(10, 10));
            browser.CaptureProbe = null;
            var selected = await browser.ExecuteAsync(new(id, new BrowserMouse("click", 680, 165, SnapshotId: shot.Observation.SnapshotId)));
            Assert.Null(selected.ErrorCode);
            Assert.Equal("Selected PUMP-1042", await page.GetByRole(AriaRole.Status).InnerTextAsync());
            Assert.Equal(0, await page.Locator("[data-agent-mask]").CountAsync());
        }
        finally { browser.CaptureProbe = null; await browser.StopAsync(default); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Graphics_require_exact_host_opt_in_and_sensitive_regions_remain_masked(bool trusted)
    {
        var options = new BrowserOptions { Enabled = true, Headless = true, FixturePort = 0,
            TrustedVisualCaptureOrigins = trusted ? ["http://127.0.0.1:5091"] : [] };
        var browser = new NativePlaywrightBrowser(options, null); await browser.StartAsync(default); var id = Guid.NewGuid();
        try
        {
            var origin = browser.HostPolicy.NavigationOrigins.Single();
            // Dynamic fixture ports do not expand the exact-origin opt-in.
            await browser.ExecuteAsync(new(id, new BrowserNavigate(origin + "/adaptive-browser.html")));
            var page = browser.ContextFor(id)!.Pages.Single();
            await page.EvaluateAsync("html => document.body.innerHTML=html", "<style>body{margin:0}</style><canvas width='100' height='40'></canvas><svg width='100' height='40'><rect width='100' height='40' fill='blue'/></svg><canvas data-sensitive width='100' height='40'></canvas>");
            await page.EvaluateAsync("() => document.querySelectorAll('canvas').forEach(e=>{const c=e.getContext('2d'); c.fillStyle='red';c.fillRect(0,0,100,40)})");
            var shot = await browser.ExecuteAsync(new(id, new BrowserScreenshot()));
            Assert.Null(shot.ErrorCode);
            using var pixels = SkiaSharp.SKBitmap.Decode(shot.Bytes);
            Assert.Equal(new SkiaSharp.SKColor(17,17,17), pixels.GetPixel(10,10));
        }
        finally { await browser.StopAsync(default); }
        if (!trusted) return;
        // Keep a disposable fixture host alive; authorize its exact dynamic origin on a separate provider.
        var fixtureHost = NewBrowser(); await fixtureHost.StartAsync(default);
        options.FixtureEnabled = false;
        options.NavigationOrigins = [fixtureHost.HostPolicy.NavigationOrigins.Single()];
        options.TrustedVisualCaptureOrigins = options.NavigationOrigins;
        browser = new NativePlaywrightBrowser(options, null); await browser.StartAsync(default); id = Guid.NewGuid();
        try
        {
            await browser.ExecuteAsync(new(id, new BrowserNavigate(options.NavigationOrigins.Single() + "/adaptive-browser.html")));
            var page = browser.ContextFor(id)!.Pages.Single();
            await page.EvaluateAsync("html => document.body.innerHTML=html", "<style>body{margin:0}</style><canvas width='100' height='40'></canvas><svg width='100' height='40'><rect width='100' height='40' fill='blue'/></svg><canvas data-sensitive width='100' height='40'></canvas>");
            await page.EvaluateAsync("() => document.querySelectorAll('canvas').forEach(e=>{const c=e.getContext('2d');c.fillStyle='red';c.fillRect(0,0,100,40)})");
            var shot = await browser.ExecuteAsync(new(id, new BrowserScreenshot())); Assert.Null(shot.ErrorCode);
            using var pixels = SkiaSharp.SKBitmap.Decode(shot.Bytes);
            Assert.Equal(SkiaSharp.SKColors.Red, pixels.GetPixel(10,10));
            Assert.Equal(SkiaSharp.SKColors.Blue, pixels.GetPixel(110,10));
            Assert.Equal(new SkiaSharp.SKColor(17,17,17), pixels.GetPixel(220,10));
            Assert.Equal(0, await page.Locator("[data-agent-mask]").CountAsync());
        }
        finally { await browser.StopAsync(default); await fixtureHost.StopAsync(default); }
    }

    [Fact]
    public async Task Unsettled_but_unchanged_pixels_do_not_authorize_coordinates()
    {
        var browser = new NativePlaywrightBrowser(new() { Enabled = true, Headless = true, FixturePort = 0,
            Limits = new() { AutomaticSettleMs = 100 } }, null);
        await browser.StartAsync(default); var id = Guid.NewGuid();
        try
        {
            await browser.ExecuteAsync(new(id, new BrowserNavigate(browser.HostPolicy.NavigationOrigins.Single() + "/adaptive-browser.html?mode=weak")));
            var shot = await browser.ExecuteAsync(new(id, new BrowserScreenshot())); Assert.Null(shot.ErrorCode);
            Assert.False(shot.Observation!.Settled);
            Assert.False(JsonSerializer.Deserialize<JsonElement>(shot.DataJson!).GetProperty("coordinateEvidence").GetBoolean());
            Assert.Equal("stale_visual_evidence", (await browser.ExecuteAsync(new(id, new BrowserMouse("click",680,165,SnapshotId:shot.Observation.SnapshotId)))).ErrorCode);
        }
        finally { await browser.StopAsync(default); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dense_capture_benchmark_preserves_privacy(bool updating)
    {
        using var logs = Microsoft.Extensions.Logging.LoggerFactory.Create(builder => Microsoft.Extensions.Logging.ConsoleLoggerExtensions.AddConsole(builder));
        var browser = new NativePlaywrightBrowser(new() { Enabled = true, Headless = true, FixturePort = 0 }, logs);
        await browser.StartAsync(default); var id = Guid.NewGuid();
        var storageRoot = Path.Combine(Path.GetTempPath(), "browser-benchmark-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storageRoot);
        var factory = new BenchmarkContexts(new DbContextOptionsBuilder<AgentCoreDbContext>()
            .UseSqlite($"Data Source={Path.Combine(storageRoot, "artifacts.db")}").Options);
        await new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync();
        var store = new SqliteArtifactStore(factory, TimeProvider.System, Path.Combine(storageRoot, "blobs"));
        try
        {
            await browser.ExecuteAsync(new(id, new BrowserNavigate(browser.HostPolicy.NavigationOrigins.Single() + "/adaptive-browser.html")));
            var page = browser.ContextFor(id)!.Pages.Single();
            await page.EvaluateAsync("html => document.body.innerHTML=html", "<div data-sensitive style='position:absolute;left:0;top:0;width:100px;height:30px;background:red'>protected</div><main></main>");
            await page.EvaluateAsync("updating => { const main=document.querySelector('main'); for(let i=0;i<10000;i++){const e=document.createElement('div');e.textContent='Grid row '+i;main.append(e)} if(updating) window.bench=setInterval(()=>main.lastChild.textContent='Tick '+Date.now(),20); }", updating);
            for (var trial=0;trial<3;trial++)
            {
                var timer=System.Diagnostics.Stopwatch.StartNew();
                var shot=await browser.ExecuteAsync(new(id,new BrowserScreenshot())); Assert.Null(shot.ErrorCode);
                var captureMs=timer.Elapsed.TotalMilliseconds;
                timer.Restart();
                var artifact=await store.CreateAsync(id,"capture.png","image/png",shot.Bytes!,null,null);
                output.WriteLine($"updating={updating} trial={trial} capture_ms={captureMs:F1} sqlite_storage_ms={timer.Elapsed.TotalMilliseconds:F1} bytes={artifact.ByteSize}");
                using var pixels=SkiaSharp.SKBitmap.Decode(shot.Bytes);
                Assert.Equal(new SkiaSharp.SKColor(17,17,17),pixels.GetPixel(10,10));
                if(updating) Assert.False(JsonSerializer.Deserialize<JsonElement>(shot.DataJson!).GetProperty("coordinateEvidence").GetBoolean());
            }
            await page.EvaluateAsync("() => clearInterval(window.bench)");
            Assert.Equal(0,await page.Locator("[data-agent-mask]").CountAsync());
        }
        finally
        {
            await browser.StopAsync(default);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(storageRoot, true);
        }
    }

    private sealed class BenchmarkContexts(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    {
        public AgentCoreDbContext CreateDbContext() => new(options);
        public Task<AgentCoreDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }

    [Theory]
    [InlineData("*")]
    [InlineData("https://example.com/path")]
    [InlineData("https://user@example.com")]
    [InlineData("https://example.com?all=true")]
    public void Visual_trust_rejects_non_origin_configuration(string origin) =>
        Assert.Throws<ArgumentException>(() => new NativePlaywrightBrowser(new() { TrustedVisualCaptureOrigins = [origin] }, null));

    private static NativePlaywrightBrowser NewBrowser() => new(new BrowserOptions { Enabled = true, Headless = true, FixtureEnabled = true,
        FixturePort = 0, InteractionMode = "InteractiveDemo", NavigationOrigins = ["http://127.0.0.1:5091"], InteractionOrigins = ["http://127.0.0.1:5091"] }, loggerFactory: null);
}
