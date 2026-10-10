using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Admin;
using AgentCore.Infrastructure.Browser;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;

namespace AgentCore.Infrastructure.Tests;

[Collection(BrowserChromiumCollection.Name)]
public sealed class BrowserPrivacyTests
{
    [Theory]
    [InlineData("Protected", true, false)]
    [InlineData("Unmasked", true, true)]
    [InlineData("Unmasked", false, false)]
    [InlineData("Disabled", true, false)]
    public async Task Chromium_pixels_enforce_effective_mode_exact_origin_and_independent_semantic_redaction(string mode, bool exact, bool exposed)
    {
        var fixture = new NativePlaywrightBrowser(new() { Enabled = true, Headless = true, FixturePort = 0 }, null);
        await fixture.StartAsync(default);
        var crossOriginFixture = new NativePlaywrightBrowser(new() { Enabled = true, Headless = true, FixturePort = 0 }, null);
        await crossOriginFixture.StartAsync(default);
        var permittedFrameOrigin = crossOriginFixture.HostPolicy.NavigationOrigins.Single();
        var origin = fixture.HostPolicy.NavigationOrigins.Single();
        var privacy = NewPrivacy(new(true, true, [exact ? origin : "http://127.0.0.1:1"], [origin]));
        await privacy.SaveAsync(0, mode, mode == "Unmasked" ? [exact ? origin : "http://127.0.0.1:1"] : [], [origin], mode == "Unmasked");
        var browser = new NativePlaywrightBrowser(new() { Enabled = true, Headless = true, FixtureEnabled = false,
            NavigationOrigins = [origin, permittedFrameOrigin], InteractionOrigins = [origin, permittedFrameOrigin] }, null, privacy: privacy);
        await browser.StartAsync(default);
        var id = Guid.NewGuid();
        try
        {
            Assert.Null((await browser.ExecuteAsync(new(id, new BrowserNavigate(origin + "/adaptive-browser.html")))).ErrorCode);
            var page = browser.ContextFor(id)!.Pages.Single();
            await page.EvaluateAsync("""
                (frameOrigin) => { document.body.innerHTML = `<style>body{margin:0}</style>
                <div data-sensitive style="position:absolute;left:0;top:0;width:100px;height:40px;background:red;color:red">private-fixture-secret</div>
                <canvas width="100" height="40" style="position:absolute;left:100px;top:0"></canvas>
                <svg width="100" height="40" style="position:absolute;left:200px;top:0"><rect width="100" height="40" fill="blue"/></svg>
                <iframe src="/adaptive-browser.html" style="position:absolute;left:300px;top:0;width:100px;height:40px;border:0"></iframe>
                <iframe src="http://127.0.0.1:1/denied" style="position:absolute;left:400px;top:0;width:100px;height:40px;border:0"></iframe>
                <iframe src="${frameOrigin}/adaptive-browser.html" style="position:absolute;left:500px;top:0;width:100px;height:40px;border:0"></iframe>
                <input type="password" value="private-password" style="position:absolute;left:0;top:80px;width:100px;height:40px;background:lime;border:0">`;
                const c=document.querySelector('canvas').getContext('2d');c.fillStyle='red';c.fillRect(0,0,100,40); }
                """, permittedFrameOrigin);
            await page.FrameLocator($"iframe[src='{permittedFrameOrigin}/adaptive-browser.html']").Locator("body").WaitForAsync();
            Assert.Contains(page.Frames, frame => frame.Url == permittedFrameOrigin + "/adaptive-browser.html");
            var screenshot = await browser.ExecuteAsync(new(id, new BrowserScreenshot()));
            if (mode == "Disabled")
            {
                Assert.Equal("forbidden", screenshot.ErrorCode); Assert.Null(screenshot.Bytes);
                var semantic = await browser.ExecuteAsync(new(id, new BrowserObserve()));
                Assert.Null(semantic.ErrorCode); Assert.DoesNotContain("private-fixture-secret", semantic.Observation!.Content);
                Assert.False(browser.HostPolicy.ScreenshotAvailable);
                var gate = new AgentCore.Infrastructure.Tools.ToolConfigurationGate(null,null,null,browser,true);
                Assert.False(gate.IsConfigured(AgentCore.Application.Tools.ToolCatalog.BrowserScreenshot));
                Assert.False(gate.IsConfigured(AgentCore.Application.Tools.ToolCatalog.BrowserVisionMouse));
                Assert.True(gate.IsConfigured(AgentCore.Application.Tools.ToolCatalog.BrowserSnapshot)); return;
            }
            Assert.Null(screenshot.ErrorCode);
            using var bitmap = SKBitmap.Decode(screenshot.Bytes);
            Assert.Equal(exposed ? SKColors.Red : new SKColor(17,17,17), bitmap.GetPixel(10,10));
            // Protected graphics trust is independent from sensitive DOM trust.
            Assert.Equal(SKColors.Red, bitmap.GetPixel(110,10)); Assert.Equal(SKColors.Blue, bitmap.GetPixel(210,10));
            Assert.Equal(SKColors.Fuchsia, bitmap.GetPixel(310,10)); Assert.Equal(SKColors.Fuchsia, bitmap.GetPixel(410,10));
            Assert.Equal(SKColors.Fuchsia, bitmap.GetPixel(510,10));
            Assert.Equal(exposed ? SKColors.Lime : new SKColor(17,17,17), bitmap.GetPixel(10,90));
            Assert.DoesNotContain("private-fixture-secret", screenshot.Observation!.Content);
            Assert.DoesNotContain("private-password", screenshot.Observation.Content);
            Assert.Equal(0, await page.Locator("[data-agent-mask]").CountAsync());
            var wrongSession = await browser.ExecuteAsync(new(Guid.NewGuid(), new BrowserMouse("click", 10,10, SnapshotId:screenshot.Observation.SnapshotId)));
            Assert.NotNull(wrongSession.ErrorCode); Assert.False(wrongSession.EffectAttempted);
        }
        finally { await browser.StopAsync(default); await fixture.StopAsync(default); await crossOriginFixture.StopAsync(default); }
    }

    [Theory]
    [InlineData("Protected")]
    [InlineData("Unmasked")]
    public async Task Closed_shadow_child_frames_fail_closed_without_pixels(string mode)
    {
        var browser = new NativePlaywrightBrowser(new() { Enabled = true, Headless = true, FixturePort = 0 }, null);
        await browser.StartAsync(default);
        var id = Guid.NewGuid();
        try
        {
            var origin = browser.HostPolicy.NavigationOrigins.Single();
            var privacy = NewPrivacy(new(true, true, [origin], []));
            await privacy.SaveAsync(0, mode, mode == "Unmasked" ? [origin] : [], [], mode == "Unmasked");
            await privacy.ActivateAtStartupAsync();
            var guarded = new NativePlaywrightBrowser(new() { Enabled = true, Headless = true, FixtureEnabled = false,
                NavigationOrigins = [origin], InteractionOrigins = [origin] }, null, privacy: privacy);
            await guarded.StartAsync(default);
            try
            {
                await guarded.ExecuteAsync(new(id, new BrowserNavigate(origin + "/adaptive-browser.html")));
                var page = guarded.ContextFor(id)!.Pages.Single();
                await page.EvaluateAsync("""
                    () => new Promise(resolve => { document.body.innerHTML = '<div id="host"></div>';
                      const root = document.querySelector('#host').attachShadow({mode:'closed'});
                      const frame = document.createElement('iframe');
                      frame.style.cssText = 'width:400px;height:200px';
                      frame.onload = resolve; frame.src = '/adaptive-browser.html'; root.appendChild(frame); })
                    """);
                var child = Assert.Single(page.Frames, frame => frame != page.MainFrame);
                await child.WaitForLoadStateAsync();
                await child.EvaluateAsync("() => document.body.innerHTML = '<div style=\"background:red;width:300px;height:100px\">private-frame-secret</div>'");
                var capture = await guarded.ExecuteAsync(new(id, new BrowserScreenshot()));
                Assert.Equal("target_denied", capture.ErrorCode);
                Assert.Null(capture.Bytes);
            }
            finally { await guarded.StopAsync(default); }
        }
        finally { await browser.StopAsync(default); }
    }

    [Fact]
    public async Task Saved_changes_do_not_split_inflight_capture_and_shutdown_revokes_unpublished_bytes()
    {
        var privacy = NewPrivacy(new(true, false, [], []));
        var browser = new NativePlaywrightBrowser(new() { Enabled = true, Headless = true, FixturePort = 0 }, null, privacy: privacy);
        await browser.StartAsync(default); var id = Guid.NewGuid();
        try
        {
            await browser.ExecuteAsync(new(id, new BrowserNavigate(browser.HostPolicy.NavigationOrigins.Single() + "/adaptive-browser.html")));
            browser.CaptureProbe = () => { privacy.SaveAsync(0, "Disabled", [], [], false).AsTask().GetAwaiter().GetResult(); browser.CaptureProbe = null; return null; };
            var pinned = await browser.ExecuteAsync(new(id, new BrowserScreenshot()));
            Assert.Null(pinned.ErrorCode); Assert.NotEmpty(pinned.Bytes!);
            var view = await privacy.ReadAsync(); Assert.True(view.RestartRequired); Assert.Equal(BrowserScreenshotPrivacyMode.Protected, view.Effective.Mode);
            var configuration = (await browser.ExecuteAsync(new(id,new BrowserGetConfig()))).DataJson!;
            Assert.Contains("\"screenshotPrivacy\":\"Protected\"",configuration);
            Assert.Contains("\"screenshotPolicyRevision\":0",configuration);
            Assert.Null((await browser.ExecuteAsync(new(id, new BrowserScreenshot()))).ErrorCode);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            browser.CaptureProbe = () => { entered.TrySetResult(); return null; };
            var capture = browser.ExecuteAsync(new(id, new BrowserScreenshot())).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await browser.StopAsync(default);
            // Closing the host cannot publish the now-retired capture.
            try { var result = await capture; Assert.Null(result.Bytes); Assert.NotNull(result.ErrorCode); }
            catch (OperationCanceledException) { }
            var restarted = new NativePlaywrightBrowser(new() { Enabled = true, Headless = true, FixturePort = 0 }, null, privacy: privacy);
            await restarted.StartAsync(default);
            try
            {
                Assert.False(restarted.HostPolicy.ScreenshotAvailable);
                Assert.False((await privacy.ReadAsync()).RestartRequired);
                await restarted.ExecuteAsync(new(id, new BrowserNavigate(restarted.HostPolicy.NavigationOrigins.Single() + "/adaptive-browser.html")));
                Assert.Equal("forbidden", (await restarted.ExecuteAsync(new(id, new BrowserScreenshot()))).ErrorCode);
                Assert.Equal("stale_visual_evidence", (await restarted.ExecuteAsync(new(id, new BrowserMouse("click",680,165,SnapshotId:pinned.Observation!.SnapshotId)))).ErrorCode);
            }
            finally { await restarted.StopAsync(default); }
        }
        finally { await browser.StopAsync(default); }
    }

    [Fact]
    public async Task Sqlite_policy_and_history_commit_atomically_survive_reopen_and_reject_stale_writes()
    {
        var path = Path.Combine(Path.GetTempPath(), "browser-privacy-" + Guid.NewGuid().ToString("N") + ".db");
        var contexts = new Contexts(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite("Data Source=" + path).Options);
        try
        {
            await new SqliteMemoryStore(contexts, TimeProvider.System).EnsureCreatedAsync();
            var ids = new SystemIdGenerator(TimeProvider.System);
            var authority = new BrowserPrivacyAuthority(true,true,["https://example.test"],[]);
            var first = new BrowserPrivacyService(new SqliteBrowserPrivacyStore(contexts,ids), authority, TimeProvider.System, ids);
            var saved = await first.SaveAsync(0,"Unmasked",["https://example.test"],[],true);
            Assert.True(saved.Durable);
            Assert.True(saved.RestartRequired);
            await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(() => first.SaveAsync(0,"Disabled",[],[],false).AsTask());
            var second = new BrowserPrivacyService(new SqliteBrowserPrivacyStore(contexts,ids),authority,TimeProvider.System,ids);
            await second.ActivateAtStartupAsync();
            Assert.Equal(BrowserScreenshotPrivacyMode.Unmasked, second.Effective.Mode); Assert.Equal(1,second.Effective.Revision);
            var history = await new SqliteAdminEventStore(contexts,ids).ListAsync(new("browserPrivacy","host"));
            Assert.Equal(AdminEventOperationKind.BrowserPrivacyChanged, Assert.Single(history).Operation);
            Assert.DoesNotContain("example.test", history.Single().SummaryJson);
            var restricted = new BrowserPrivacyService(new SqliteBrowserPrivacyStore(contexts,ids),new(false,false,[],[]),TimeProvider.System,ids);
            await restricted.ActivateAtStartupAsync();
            Assert.Equal(BrowserScreenshotPrivacyMode.Disabled,restricted.Effective.Mode); Assert.Empty(restricted.Effective.UnmaskedOrigins);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(path); }
    }

    [Theory]
    [InlineData("unmasked", BrowserScreenshotPrivacyMode.Protected)]
    [InlineData("origins", BrowserScreenshotPrivacyMode.Unmasked)]
    [InlineData("graphics", BrowserScreenshotPrivacyMode.Unmasked)]
    [InlineData("capture", BrowserScreenshotPrivacyMode.Disabled)]
    [InlineData("unchanged", BrowserScreenshotPrivacyMode.Unmasked)]
    public async Task Sqlite_startup_distinguishes_deployment_constraints_from_pending_restart(string restriction, BrowserScreenshotPrivacyMode expectedMode)
    {
        var path = Path.Combine(Path.GetTempPath(), $"privacy-ceiling-{Guid.NewGuid():N}.db");
        var contexts = new Contexts(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite("Data Source=" + path).Options);
        var ids = new SystemIdGenerator(TimeProvider.System);
        string[] origins = ["https://first.test", "https://second.test"];
        try
        {
            await new SqliteMemoryStore(contexts, TimeProvider.System).EnsureCreatedAsync();
            var initial = new BrowserPrivacyService(new SqliteBrowserPrivacyStore(contexts, ids), new(true, true, origins, origins), TimeProvider.System, ids);
            var pending = await initial.SaveAsync(0, "Unmasked", origins, origins, true);
            Assert.True(pending.RestartRequired);
            Assert.False(pending.ConstrainedByDeployment);
            var authority = new BrowserPrivacyAuthority(restriction != "capture", restriction != "unmasked",
                restriction == "origins" ? [origins[0]] : origins, restriction == "graphics" ? [origins[0]] : origins);
            var reopened = new BrowserPrivacyService(new SqliteBrowserPrivacyStore(contexts, ids), authority, TimeProvider.System, ids);
            await reopened.ActivateAtStartupAsync();
            var view = await reopened.ReadAsync();
            Assert.Equal(expectedMode, view.Effective.Mode);
            Assert.Equal(view.Saved.Revision, view.Effective.Revision);
            Assert.False(view.RestartRequired);
            Assert.Equal(restriction != "unchanged", view.ConstrainedByDeployment);
            Assert.Equal(origins, view.Saved.UnmaskedOrigins);
            Assert.Equal(restriction == "origins" ? [origins[0]] : origins, view.Effective.UnmaskedOrigins);
            Assert.Equal(restriction == "graphics" ? [origins[0]] : origins, view.Effective.TrustedGraphicsOrigins);
            if (view.ConstrainedByDeployment) Assert.Contains("Restarting alone cannot remove", view.Activation);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(path); }
    }

    private static BrowserPrivacyService NewPrivacy(BrowserPrivacyAuthority authority)
    { var ids = new SystemIdGenerator(TimeProvider.System); return new(new InMemoryBrowserPrivacyStore(new InMemoryAdminEventStore(ids)), authority, TimeProvider.System, ids); }
    private sealed class Contexts(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    { public AgentCoreDbContext CreateDbContext() => new(options); public Task<AgentCoreDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext()); }
}
