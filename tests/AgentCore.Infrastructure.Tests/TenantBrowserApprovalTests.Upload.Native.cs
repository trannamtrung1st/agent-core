using System.Text;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Browser;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantBrowserApprovalTests
{
    private sealed class UploadChromiumTheoryAttribute : TheoryAttribute
    {
        public UploadChromiumTheoryAttribute() { if (Environment.GetEnvironmentVariable("AGENTCORE_CHROMIUM_ISOLATION_TESTS") != "1") Skip = "Explicit disposable native Chromium opt-in required."; }
    }
    [UploadChromiumTheory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Browser_upload_native_consent_to_actual_file_input_preserves_original_bytes_and_never_replays(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true);
        var s = await Prepare(f); var (file, _) = await UploadSource(f, s);
        var root = Path.Combine(f.Root, "upload-native");
        var browser = new NativePlaywrightBrowser(new() { Enabled = true, Headless = true, FixtureEnabled = true, FixturePort = 0,
            ProfileRoot = root, ProfileMode = "EphemeralSession" }, null);
        await browser.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(browser.IsAvailable); var owner = new TenantBrowserOwner(s.Tenant, s.Agent.Instance.InstanceId, s.Agent.PrincipalId, s.Run.SessionId);
            Assert.Null(await browser.BindTenantIsolationAsync(owner, BrowserProfileMode.EphemeralSession));
            var navigation = new BrowserNavigate(browser.Fixture.Origin + "/upload");
            var prepared = await browser.PrepareTenantCommandAsync(owner, navigation); Assert.Null(prepared.ErrorCode);
            Assert.Null((await browser.ExecuteAsync(new(s.Run.SessionId, navigation) { TenantOwner = owner, TenantWitness = prepared.Witness })).ErrorCode);
            (s, var request) = await ApprovedEffect(f, s, browser, tool: ToolCatalog.BrowserUpload, args: UploadArguments(file.ArtifactId));
            Assert.Equal(browser.Fixture.Origin, request.Operation.BrowserCommand!.FileInputOrigin);
            var result = await ExecuteEffect(Effects(f, browser), s, request);
            Assert.Equal(TenantOperationOutcome.Completed, result.Receipt.Outcome); Assert.NotNull(result.Result);
            var page = browser.ContextFor(s.Run.SessionId)!.Pages.Single();
            Assert.Equal(file.DisplayName, await page.Locator("#chosen").InnerTextAsync());
            Assert.Equal("Private original thử nghiệm", await page.Locator("input[type=file]").EvaluateAsync<string>("async el => await el.files[0].text()"));
            Assert.Equal(file.ByteSize, await page.Locator("input[type=file]").EvaluateAsync<long>("el => el.files[0].size"));
            await page.EvaluateAsync("() => { document.querySelector('input[type=file]').value=''; document.querySelector('#chosen').textContent='cleared'; }");
            Assert.Null((await ExecuteEffect(Effects(f, browser), s, request)).Result);
            Assert.Equal("cleared", await page.Locator("#chosen").InnerTextAsync());
            Assert.Equal(0, await page.Locator("input[type=file]").EvaluateAsync<int>("el => el.files.length"));
        }
        finally { await browser.StopAsync(CancellationToken.None); }
    }
}
