using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Browser;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantBrowserOwnershipTests
{
    private sealed class TenantChromiumTheoryAttribute : TheoryAttribute
    {
        public TenantChromiumTheoryAttribute() { if (Environment.GetEnvironmentVariable("AGENTCORE_CHROMIUM_ISOLATION_TESTS") != "1") Skip = "Explicit disposable native Chromium opt-in required."; }
    }

    [TenantChromiumFact]
    public async Task Tenant_password_frame_origin_is_exact_and_changed_iframe_cannot_resolve_the_old_secret()
    {
        await using var first = new LoopbackBrowserFixtureHost(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        await using var second = new LoopbackBrowserFixtureHost(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        await first.StartAsync(0, CancellationToken.None); await second.StartAsync(0, CancellationToken.None);
        Assert.True(first.IsAvailable); Assert.True(second.IsAvailable);
        var root = Path.Combine(Path.GetTempPath(), "tenant-password-frame-" + Guid.NewGuid().ToString("N"));
        var browser = new NativePlaywrightBrowser(new BrowserOptions { Enabled = true, Headless = true, FixtureEnabled = false, ProfileRoot = root,
            ProfileMode = "EphemeralSession", PolicyMode = "Restricted", NavigationOrigins = [first.Origin!, second.Origin!],
            InteractionOrigins = [first.Origin!, second.Origin!], ResourceOrigins = [first.Origin!, second.Origin!] }, null);
        await browser.StartAsync(CancellationToken.None);
        try
        {
            var owner = Owner(); Assert.Null(await browser.BindTenantIsolationAsync(owner, BrowserProfileMode.EphemeralSession));
            Assert.Null((await Dispatch(browser, owner, new BrowserNavigate(first.Origin + "/browser-native.html"))).ErrorCode);
            var page = browser.ContextFor(owner.SessionId)!.Pages.Single();
            await page.SetContentAsync("<iframe title='Login' src='" + second.Origin + "/credential-login'></iframe>");
            await page.FrameLocator("iframe").GetByLabel("Password", new() { Exact = true }).WaitForAsync();
            var snapshot = await Dispatch(browser, owner, new BrowserObserve()); var frameRef = Assert.Single(snapshot.Observation!.Frames!).Ref;
            var command = new BrowserFillCredential(new("label", "Password", FrameRef: frameRef), "account");
            var prepared = await browser.PrepareTenantCommandAsync(owner, command); Assert.Null(prepared.ErrorCode);
            Assert.Equal(first.Origin, prepared.Origin); Assert.Equal(second.Origin, prepared.Witness!.CredentialOrigin);
            var frame = page.Frames.Single(f => f != page.MainFrame); await frame.GotoAsync(first.Origin + "/credential-login");
            var resolved = false;
            var result = await browser.ExecuteAsync(new(owner.SessionId, command) { TenantOwner = owner, TenantWitness = prepared.Witness,
                TenantDispatchCheck = _ => ValueTask.FromResult<string?>(null),
                TenantPasswordResolver = (_, _) => { resolved = true; return ValueTask.FromResult("old-frame-private-password"); } });
            Assert.Equal("browser_target_changed", result.ErrorCode); Assert.False(resolved); Assert.False(result.EffectAttempted);
            Assert.Equal("", await frame.GetByLabel("Password", new() { Exact = true }).InputValueAsync());
        }
        finally { await browser.StopAsync(CancellationToken.None); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [TenantChromiumFact]
    public async Task Tenant_password_sink_requires_exact_owned_command_and_internal_resolver_and_never_exposes_the_value()
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-password-" + Guid.NewGuid().ToString("N")); var browser = Native(root);
        await browser.StartAsync(CancellationToken.None);
        try
        {
            var owner = Owner(); await Open(browser, owner); var page = Page(browser, owner);
            await page.GotoAsync(browser.Fixture.Origin + "/credential-login?reflect=1");
            var command = new BrowserFillCredential(new("label", "Password"), "account"); var prepared = await browser.PrepareTenantCommandAsync(owner, command);
            Assert.Null(prepared.ErrorCode); Assert.Equal(browser.Fixture.Origin, prepared.Witness!.CredentialOrigin);
            const string secret = "tenant-private-password-59327"; var resolutions = 0; var checks = 0;
            var request = new BrowserRequest(owner.SessionId, command) { TenantOwner = owner, TenantWitness = prepared.Witness,
                TenantDispatchCheck = _ => { checks++; return ValueTask.FromResult<string?>(null); },
                TenantPasswordResolver = (origin, _) => { Assert.Equal(browser.Fixture.Origin, origin); resolutions++; return ValueTask.FromResult(secret); } };
            Assert.DoesNotContain(nameof(BrowserRequest.TenantPasswordResolver), JsonSerializer.Serialize(request));
            Assert.Equal("forbidden", (await browser.ExecuteAsync(request with { TenantPasswordResolver = null })).ErrorCode);
            Assert.Equal("forbidden", (await browser.ExecuteAsync(request with { TenantDispatchCheck = null })).ErrorCode);
            Assert.Equal("forbidden", (await browser.ExecuteAsync(request with { TenantOwner = null })).ErrorCode);
            Assert.Equal("forbidden", (await browser.ExecuteAsync(request with { TenantOwner = owner with { TenantId = Guid.NewGuid() } })).ErrorCode);
            Assert.Equal("forbidden", (await browser.ExecuteAsync(request with { Command = new BrowserObserve() })).ErrorCode);
            Assert.Equal(0, resolutions); Assert.Equal(0, checks);
            Assert.Equal(prepared.Witness, (await browser.PrepareTenantCommandAsync(owner, command)).Witness);
            var filled = await browser.ExecuteAsync(request); Assert.Null(filled.ErrorCode); Assert.True(filled.EffectConfirmedBySdk);
            Assert.Equal(1, resolutions); Assert.Equal(2, checks); Assert.DoesNotContain(secret, JsonSerializer.Serialize(filled));
            Assert.Equal(secret, await page.GetByLabel("Password", new() { Exact = true }).InputValueAsync());
            Assert.DoesNotContain(secret, JsonSerializer.Serialize(await Dispatch(browser, owner, new BrowserObserve())));
            Assert.Equal("browser_target_changed", (await browser.ExecuteAsync(request)).ErrorCode); Assert.Equal(1, resolutions);
        }
        finally { await browser.StopAsync(CancellationToken.None); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TenantChromiumTheory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_password_sink_rechecks_authority_and_live_field_after_resolution_before_any_fill(bool changeField)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-password-race-" + Guid.NewGuid().ToString("N")); var browser = Native(root);
        await browser.StartAsync(CancellationToken.None);
        try
        {
            var owner = Owner(); await Open(browser, owner); var page = Page(browser, owner);
            await page.GotoAsync(browser.Fixture.Origin + "/credential-login");
            var command = new BrowserFillCredential(new("label", "Password"), "account"); var prepared = await browser.PrepareTenantCommandAsync(owner, command);
            var resolved = false;
            var result = await browser.ExecuteAsync(new(owner.SessionId, command) { TenantOwner = owner, TenantWitness = prepared.Witness,
                TenantDispatchCheck = _ => ValueTask.FromResult<string?>(resolved ? "permission_denied" : null),
                TenantPasswordResolver = async (_, _) => { resolved = true; if (changeField) await page.GetByLabel("Password", new() { Exact = true }).EvaluateAsync("el => el.type='text'"); return "race-private-password"; } });
            Assert.Equal(changeField ? "forbidden" : "permission_denied", result.ErrorCode);
            Assert.True(resolved); Assert.False(result.EffectAttempted); Assert.False(result.EffectConfirmedBySdk);
            Assert.Equal("", await page.GetByLabel("Password", new() { Exact = true }).InputValueAsync());
        }
        finally { await browser.StopAsync(CancellationToken.None); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
