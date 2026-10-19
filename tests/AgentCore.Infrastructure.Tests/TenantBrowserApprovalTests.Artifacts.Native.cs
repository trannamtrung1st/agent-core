using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Browser;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantBrowserApprovalTests
{
    [UploadChromiumTheory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Browser_capture_native_gate_publishes_actual_masked_image_without_reentry_or_replay(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true);
        var s = await Prepare(f); await GrantBrowserPublication(f, s);
        var browser = new NativePlaywrightBrowser(new() { Enabled = true, Headless = true, FixtureEnabled = true, FixturePort = 0,
            ProfileRoot = Path.Combine(f.Root, "capture-native"), ProfileMode = "EphemeralSession" }, null);
        await browser.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(browser.IsAvailable); var owner = new TenantBrowserOwner(s.Tenant, s.Agent.Instance.InstanceId, s.Agent.PrincipalId, s.Run.SessionId);
            Assert.Null(await browser.BindTenantIsolationAsync(owner, BrowserProfileMode.EphemeralSession));
            var navigation = new BrowserNavigate(browser.Fixture.Origin + "/browser-native.html");
            var prepared = await browser.PrepareTenantCommandAsync(owner, navigation); Assert.Null(prepared.ErrorCode);
            Assert.Null((await browser.ExecuteAsync(new(s.Run.SessionId, navigation) { TenantOwner = owner, TenantWitness = prepared.Witness })).ErrorCode);
            var page = browser.ContextFor(s.Run.SessionId)!.Pages.Single();
            await page.SetContentAsync("<label>Password<input type='password' value='capture-private-value'></label><button>Ready</button>");
            (s, var request) = await ApprovedEffect(f, s, browser, tool: ToolCatalog.BrowserScreenshot, args: "{}");
            var result = await ExecuteEffect(Effects(f, browser), s, request);
            Assert.Equal(TenantOperationOutcome.Completed, result.Receipt.Outcome); Assert.NotNull(result.Result); Assert.Null(result.Result.Bytes);
            Assert.True(result.Result.RedactionCount > 0); Assert.True(result.Result.Width > 0); Assert.True(result.Result.Height > 0);
            var publication = TenantBrowserArtifactRules.Read(result.Receipt.BrowserArtifactsJson!); Assert.Equal("published", publication.Status);
            var artifact = Assert.Single(publication.Files); var bytes = await ((ITenantArtifactStore)f.Artifacts).ReadTenantContentAsync(s.Tenant, s.Run.SessionId, artifact.ArtifactId);
            Assert.True(bytes.Length > 100); Assert.True(bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
            Assert.Null((await ExecuteEffect(Effects(f, browser), s, request)).Result);
            Assert.Single((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(s.Tenant, s.Run.SessionId, null, null, 100)).Items);
        }
        finally { await browser.StopAsync(CancellationToken.None); }
    }
    [UploadChromiumTheory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Browser_download_native_click_saves_the_actual_file_under_exact_opt_in_consent(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true);
        var s = await Prepare(f); await GrantBrowserPublication(f, s);
        var browser = new NativePlaywrightBrowser(new() { Enabled = true, Headless = true, FixtureEnabled = true, FixturePort = 0,
            ProfileRoot = Path.Combine(f.Root, "download-native"), ProfileMode = "EphemeralSession" }, null);
        await browser.StartAsync(CancellationToken.None);
        try
        {
            var owner = new TenantBrowserOwner(s.Tenant, s.Agent.Instance.InstanceId, s.Agent.PrincipalId, s.Run.SessionId);
            Assert.Null(await browser.BindTenantIsolationAsync(owner, BrowserProfileMode.EphemeralSession));
            var navigation = new BrowserNavigate(browser.Fixture.Origin + "/browser-native.html");
            var prepared = await browser.PrepareTenantCommandAsync(owner, navigation);
            Assert.Null((await browser.ExecuteAsync(new(s.Run.SessionId, navigation) { TenantOwner = owner, TenantWitness = prepared.Witness })).ErrorCode);
            (s, var request) = await ApprovedEffect(f, s, browser, tool: ToolCatalog.BrowserClick,
                args: """{"target":{"by":"role","value":"link","name":"Download note"},"clickCount":1}""", downloads: true);
            var result = await ExecuteEffect(Effects(f, browser), s, request);
            Assert.Equal(TenantOperationOutcome.Completed, result.Receipt.Outcome); Assert.Null(result.Result!.Downloads);
            var file = Assert.Single(TenantBrowserArtifactRules.Read(result.Receipt.BrowserArtifactsJson!).Files);
            var record = await ((ITenantArtifactStore)f.Artifacts).FindTenantAsync(s.Tenant, s.Run.SessionId, file.ArtifactId);
            Assert.Equal("browser-note.txt", record!.DisplayName);
            Assert.Equal("Generic browser fixture note.", System.Text.Encoding.UTF8.GetString(await ((ITenantArtifactStore)f.Artifacts).ReadTenantContentAsync(s.Tenant, s.Run.SessionId, file.ArtifactId)));
            Assert.Null((await ExecuteEffect(Effects(f, browser), s, request)).Result);
            Assert.Single((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(s.Tenant, s.Run.SessionId, null, null, 100)).Items);
        }
        finally { await browser.StopAsync(CancellationToken.None); }
    }
}
