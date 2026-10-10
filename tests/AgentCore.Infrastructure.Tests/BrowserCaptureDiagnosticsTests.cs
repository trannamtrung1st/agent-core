using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Browser;
using Microsoft.Playwright;

namespace AgentCore.Infrastructure.Tests;

[Collection(BrowserChromiumCollection.Name)]
public sealed class BrowserCaptureDiagnosticsTests
{
    [Theory]
    [InlineData("dom", "dom_mutation_during_settle")]
    [InlineData("network", "network_inflight_observed")]
    [InlineData("animation", "animation_running")]
    public async Task Dynamic_page_explains_unavailable_coordinates_and_semantic_controls_remain_usable(string mode, string reason)
    {
        var browser = new NativePlaywrightBrowser(new() { Enabled = true, Headless = true, FixturePort = 0 }, null);
        await browser.StartAsync(default); var id = Guid.NewGuid();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Assert.Null((await browser.ExecuteAsync(BrowserTestRequests.Navigate(id, new Uri(browser.HostPolicy.NavigationOrigins.Single() + "/")))).ErrorCode);
            var page = browser.ContextFor(id)!.Pages[0];
            await page.EvaluateAsync("html => { document.body.innerHTML = html; }", "<button onclick=\"this.textContent='Done'\">Stable control</button><p id='tick'>0</p>");
            if (mode == "dom") await page.EvaluateAsync("() => { let n=0; setInterval(() => document.querySelector('#tick').textContent=String(++n), 50); }");
            if (mode == "network")
            {
                await page.RouteAsync("**/pending", async route => { await release.Task; await route.FulfillAsync(new() { Body = "done" }); });
                await page.EvaluateAsync("() => { void fetch('/pending'); }");
            }
            if (mode == "animation") await page.EvaluateAsync("() => document.querySelector('#tick').animate([{opacity:1},{opacity:.5}], {duration:1000,iterations:Infinity})");
            var captured = await browser.ExecuteAsync(new(id, new BrowserScreenshot()));
            Assert.Null(captured.ErrorCode); Assert.NotEmpty(captured.Bytes!);
            using var json = JsonDocument.Parse(captured.DataJson!);
            var root = json.RootElement;
            Assert.False(root.GetProperty("coordinateEvidence").GetBoolean());
            Assert.Contains(reason, root.GetProperty("coordinateEvidenceUnavailableReasons").EnumerateArray().Select(v => v.GetString()));
            var diagnostics = root.GetProperty("captureDiagnostics");
            if (mode == "dom") Assert.True(diagnostics.GetProperty("settlement").GetProperty("domChangeSamples").GetInt32() > 0);
            if (mode == "network") Assert.True(diagnostics.GetProperty("inflightAtCapture").GetInt32() > 0);
            if (mode == "animation") Assert.True(diagnostics.GetProperty("runningAnimations").GetInt32() > 0);
            var mouse = await browser.ExecuteAsync(new(id, new BrowserMouse("click", 20, 20, SnapshotId: captured.Observation!.SnapshotId)));
            Assert.Equal("stale_visual_evidence", mouse.ErrorCode); Assert.False(mouse.EffectAttempted);
            Assert.Null((await browser.ExecuteAsync(new(id, new BrowserClick(new("role", "button", Name: "Stable control"))))).ErrorCode);
            Assert.Equal("Done", await page.GetByRole(AriaRole.Button).InnerTextAsync());
            var closed = await browser.ExecuteAsync(new(id, new BrowserClose()));
            Assert.Null(closed.ErrorCode); Assert.True(page.IsClosed); Assert.False(closed.ApplicationOutcomeVerified);
        }
        finally { release.TrySetResult(); await browser.StopAsync(default); }
    }

    [Theory]
    [InlineData("dom", "visual_state_changed_during_capture")]
    [InlineData("viewport", "viewport_changed_during_capture")]
    [InlineData("navigation", "visual_state_changed_during_capture")]
    [InlineData("fonts", "fonts_loading")]
    [InlineData("observer", "state_observation_unavailable")]
    public async Task Capture_consistency_changes_do_not_confer_authority(string mode, string reason)
    {
        var browser = new NativePlaywrightBrowser(new() { Enabled = true, Headless = true, FixturePort = 0 }, null);
        await browser.StartAsync(default); var id = Guid.NewGuid();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var url = browser.HostPolicy.NavigationOrigins.Single() + "/";
            Assert.Null((await browser.ExecuteAsync(BrowserTestRequests.Navigate(id, new Uri(url)))).ErrorCode);
            var initial = await browser.ExecuteAsync(new(id, new BrowserScreenshot()));
            using (var json = JsonDocument.Parse(initial.DataJson!)) Assert.True(json.RootElement.GetProperty("coordinateEvidence").GetBoolean());
            if (mode == "fonts") await browser.ContextFor(id)!.Pages[0].RouteAsync("**/pending-font", async route => { await release.Task; await route.AbortAsync(); });
            browser.CaptureConsistencyProbe = page => mode switch
            {
                "observer" => page.EvaluateAsync("() => { delete window.__acSettle; }"),
                "fonts" => page.EvaluateAsync("() => { const font = new FontFace('Pending', 'url(/pending-font)'); document.fonts.add(font); void font.load().catch(() => {}); }"),
                "viewport" => page.SetViewportSizeAsync(1000, 600),
                "navigation" => page.GotoAsync(url),
                _ => page.EvaluateAsync("() => { document.body.append(document.createElement('p')); }")
            };
            var changed = await browser.ExecuteAsync(new(id, new BrowserScreenshot()));
            Assert.Null(changed.ErrorCode);
            using (var json = JsonDocument.Parse(changed.DataJson!))
            {
                Assert.False(json.RootElement.GetProperty("coordinateEvidence").GetBoolean());
                if (mode == "navigation") Assert.True(json.RootElement.GetProperty("captureDiagnostics").GetProperty("pageChanged").GetBoolean());
                Assert.Contains(reason, json.RootElement.GetProperty("coordinateEvidenceUnavailableReasons").EnumerateArray().Select(v => v.GetString()));
            }
            Assert.Equal("stale_visual_evidence", (await browser.ExecuteAsync(new(id, new BrowserMouse("click", 20, 20, SnapshotId: initial.Observation!.SnapshotId)))).ErrorCode);
            browser.CaptureConsistencyProbe = null;
            release.TrySetResult();
            if (mode == "fonts") await browser.ContextFor(id)!.Pages[0].EvaluateAsync("() => document.fonts.ready.then(() => undefined)");
            if (mode == "observer") await browser.ContextFor(id)!.Pages[0].GotoAsync(url);
            var recovered = await browser.ExecuteAsync(new(id, new BrowserScreenshot()));
            using var recovery = JsonDocument.Parse(recovered.DataJson!);
            Assert.True(recovery.RootElement.GetProperty("coordinateEvidence").GetBoolean());
            Assert.Empty(recovery.RootElement.GetProperty("coordinateEvidenceUnavailableReasons").EnumerateArray());
        }
        finally { release.TrySetResult(); await browser.StopAsync(default); }
    }
}
