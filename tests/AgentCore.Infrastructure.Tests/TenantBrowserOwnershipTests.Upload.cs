using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Browser;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantBrowserOwnershipTests
{
    [TenantChromiumFact]
    public async Task Tenant_upload_file_input_origin_is_exact_for_cross_origin_frames_and_cannot_change_before_resolution()
    {
        await using var first = new LoopbackBrowserFixtureHost(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        await using var second = new LoopbackBrowserFixtureHost(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        await first.StartAsync(0, CancellationToken.None); await second.StartAsync(0, CancellationToken.None);
        var root = Path.Combine(Path.GetTempPath(), "tenant-upload-frame-" + Guid.NewGuid().ToString("N"));
        var browser = new NativePlaywrightBrowser(new() { Enabled = true, Headless = true, FixtureEnabled = false, ProfileRoot = root,
            ProfileMode = "EphemeralSession", PolicyMode = "Restricted", NavigationOrigins = [first.Origin!, second.Origin!],
            InteractionOrigins = [first.Origin!, second.Origin!], ResourceOrigins = [first.Origin!, second.Origin!] }, null);
        await browser.StartAsync(CancellationToken.None);
        try
        {
            var owner = Owner(); Assert.Null(await browser.BindTenantIsolationAsync(owner, BrowserProfileMode.EphemeralSession));
            Assert.Null((await Dispatch(browser, owner, new BrowserNavigate(first.Origin + "/browser-native.html"))).ErrorCode);
            var page = browser.ContextFor(owner.SessionId)!.Pages.Single();
            await page.SetContentAsync("<iframe title='File input' src='" + second.Origin + "/upload'></iframe>");
            await page.FrameLocator("iframe").GetByLabel("Product image").WaitForAsync();
            var snapshot = await Dispatch(browser, owner, new BrowserObserve()); var frameRef = Assert.Single(snapshot.Observation!.Frames!).Ref;
            var command = new BrowserUploadCommand(new("label", "Product image", FrameRef: frameRef), [Guid.NewGuid().ToString("D")]);
            var prepared = await browser.PrepareTenantCommandAsync(owner, command); Assert.Null(prepared.ErrorCode);
            Assert.Equal(first.Origin, prepared.Origin); Assert.Equal(second.Origin, prepared.Witness!.FileInputOrigin);
            var frame = page.Frames.Single(f => f != page.MainFrame); await frame.GotoAsync(first.Origin + "/upload");
            var resolved = false;
            var result = await browser.ExecuteAsync(new(owner.SessionId, command) { TenantOwner = owner, TenantWitness = prepared.Witness,
                TenantDispatchCheck = _ => ValueTask.FromResult<string?>(null),
                TenantUploadResolver = _ => { resolved = true; return ValueTask.FromResult<IReadOnlyList<BrowserUpload>>([new("private.txt", "text/plain", "private"u8.ToArray())]); } });
            Assert.Equal("browser_target_changed", result.ErrorCode); Assert.False(resolved); Assert.False(result.EffectAttempted);
            Assert.Equal(0, await frame.Locator("input[type=file]").EvaluateAsync<int>("el => el.files.length"));
        }
        finally { await browser.StopAsync(CancellationToken.None); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TenantChromiumTheory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_upload_sink_rechecks_authority_and_live_frame_after_resolving_files(bool navigate)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-upload-sink-" + Guid.NewGuid().ToString("N")); var browser = Native(root);
        await browser.StartAsync(CancellationToken.None);
        try
        {
            var owner = Owner(); await Open(browser, owner); var page = Page(browser, owner);
            await page.GotoAsync(browser.Fixture.Origin + "/upload");
            var command = new BrowserUploadCommand(new("label", "Product image"), [Guid.NewGuid().ToString("D")]);
            var prepared = await browser.PrepareTenantCommandAsync(owner, command); Assert.Null(prepared.ErrorCode);
            var resolutions = 0; var checks = 0;
            var request = new BrowserRequest(owner.SessionId, command) { TenantOwner = owner, TenantWitness = prepared.Witness,
                TenantDispatchCheck = _ => { checks++; return ValueTask.FromResult<string?>(resolutions == 0 || navigate ? null : "permission_denied"); },
                TenantUploadResolver = async _ => {
                    resolutions++;
                    if (navigate) await page.GotoAsync(browser.Fixture.Origin + "/upload?changed=1");
                    return [new("source.txt", "text/plain", "original"u8.ToArray())];
                } };
            Assert.DoesNotContain(nameof(BrowserRequest.TenantUploadResolver), JsonSerializer.Serialize(request));
            Assert.Equal("forbidden", (await browser.ExecuteAsync(request with { TenantUploadResolver = null })).ErrorCode);
            Assert.Equal("forbidden", (await browser.ExecuteAsync(request with { TenantDispatchCheck = null })).ErrorCode);
            Assert.Equal(0, resolutions);
            var result = await browser.ExecuteAsync(request);
            Assert.Equal(navigate ? "browser_target_changed" : "permission_denied", result.ErrorCode); Assert.False(result.EffectAttempted);
            Assert.Equal(1, resolutions); Assert.Equal(navigate ? 1 : 2, checks);
            Assert.Equal(0, await page.Locator("input[type=file]").EvaluateAsync<int>("el => el.files.length"));
            Assert.Equal(navigate ? "browser_target_changed" : "permission_denied", (await browser.ExecuteAsync(request)).ErrorCode); Assert.Equal(1, resolutions);
        }
        finally { await browser.StopAsync(CancellationToken.None); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
