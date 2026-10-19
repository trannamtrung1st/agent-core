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
    [UploadChromiumTheory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_Browser_upload_native_consent_to_actual_file_input_preserves_original_bytes_and_never_replays(bool sqlite, bool drop)
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
            var page = browser.ContextFor(s.Run.SessionId)!.Pages.Single();
            if (drop) await page.SetContentAsync("""
                <div data-testid="file-drop">Drop exact file</div><output id="chosen"></output>
                <script>window.dropCount=0; document.querySelector('[data-testid=file-drop]').addEventListener('drop', async e => {
                    e.preventDefault(); window.dropCount++; window.droppedFile=e.dataTransfer.files[0];
                    document.querySelector('#chosen').textContent=window.droppedFile.name;
                });</script>
                """);
            (s, var request) = await ApprovedEffect(f, s, browser, tool: FileTransferTool(drop), args: FileTransferArguments(drop, file.ArtifactId));
            Assert.Equal(browser.Fixture.Origin, request.Operation.BrowserCommand!.FileInputOrigin);
            var result = await ExecuteEffect(Effects(f, browser), s, request);
            Assert.Equal(TenantOperationOutcome.Completed, result.Receipt.Outcome); Assert.NotNull(result.Result);
            Assert.Equal(file.DisplayName, await page.Locator("#chosen").InnerTextAsync());
            Assert.Equal("Private original thử nghiệm", await page.EvaluateAsync<string>(drop ? "async () => await window.droppedFile.text()" : "async () => await document.querySelector('input[type=file]').files[0].text()"));
            Assert.Equal(file.ByteSize, await page.EvaluateAsync<long>(drop ? "() => window.droppedFile.size" : "() => document.querySelector('input[type=file]').files[0].size"));
            Assert.Equal(file.ContentType, await page.EvaluateAsync<string>(drop ? "() => window.droppedFile.type" : "() => document.querySelector('input[type=file]').files[0].type"));
            if (drop) await page.EvaluateAsync("() => { window.droppedFile=null; document.querySelector('#chosen').textContent='cleared'; }");
            else await page.EvaluateAsync("() => { document.querySelector('input[type=file]').value=''; document.querySelector('#chosen').textContent='cleared'; }");
            Assert.Null((await ExecuteEffect(Effects(f, browser), s, request)).Result);
            Assert.Equal("cleared", await page.Locator("#chosen").InnerTextAsync());
            if (drop) { Assert.Equal(1, await page.EvaluateAsync<int>("() => window.dropCount")); Assert.Null(await page.EvaluateAsync<object?>("() => window.droppedFile")); }
            else Assert.Equal(0, await page.Locator("input[type=file]").EvaluateAsync<int>("el => el.files.length"));
        }
        finally { await browser.StopAsync(CancellationToken.None); }
    }
}
