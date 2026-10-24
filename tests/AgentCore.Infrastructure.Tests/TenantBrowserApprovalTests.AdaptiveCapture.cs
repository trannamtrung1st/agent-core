using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Browser;
using SkiaSharp;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantBrowserApprovalTests
{
    [UploadChromiumTheory]
    [InlineData(false, false, false, false)] [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)] [InlineData(true, true, false, false)]
    [InlineData(false, true, true, false)] [InlineData(true, true, true, false)]
    [InlineData(false, false, true, false)] [InlineData(true, false, true, false)]
    [InlineData(false, true, true, true)] [InlineData(true, true, true, true)]
    [InlineData(false, false, true, true)] [InlineData(true, false, true, true)]
    public async Task Tenant_native_capture_preserves_masked_context_and_current_image_read_when_semantics_fail_or_visual_state_changes(bool sqlite, bool vision, bool unavailable, bool changed)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true); var s = await Prepare(f);
        await GrantBrowserPublication(f, s, read: true);
        var browser = new NativePlaywrightBrowser(new() { Enabled = true, Headless = true, FixtureEnabled = true, FixturePort = 0,
            ProfileRoot = Path.Combine(f.Root, "adaptive-capture"), ProfileMode = "EphemeralSession", Limits = new(CapturesPerScope: 1) }, null);
        await browser.StartAsync(default);
        try
        {
            var owner = new TenantBrowserOwner(s.Tenant, s.Agent.Instance.InstanceId, s.Agent.PrincipalId, s.Run.SessionId);
            Assert.Null(await browser.BindTenantIsolationAsync(owner, BrowserProfileMode.EphemeralSession));
            var navigation = new BrowserNavigate(browser.Fixture.Origin + "/adaptive-browser.html?mode=accessible");
            var setup = await browser.PrepareTenantCommandAsync(owner, navigation);
            Assert.Null((await browser.ExecuteAsync(new(s.Run.SessionId, navigation) { TenantOwner = owner, TenantWitness = setup.Witness })).ErrorCode);
            var page = browser.ContextFor(s.Run.SessionId)!.Pages.Single();
            await page.EvaluateAsync("() => { const f=document.createElement('input'); f.setAttribute('data-sensitive',''); f.value='protected-visible-value'; Object.assign(f.style,{position:'absolute',left:'20px',top:'20px',width:'160px',height:'30px'}); document.body.appendChild(f); }");
            var box = (await page.Locator("[data-sensitive]").BoundingBoxAsync())!;
            (s, var missing) = await ApprovedEffect(f, s, browser, tool: ToolCatalog.BrowserScreenshot,
                args: """{"target":{"by":"role","value":"button","name":"Missing"}}""");
            var failed = await ExecuteEffect(Effects(f, browser), s, missing);
            Assert.Equal(TenantOperationOutcome.Denied, failed.Receipt.Outcome); Assert.Equal("target_missing", failed.Receipt.ReasonCode); Assert.Null(failed.Result);
            Assert.Empty((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(s.Tenant, s.Run.SessionId, null, null, 100)).Items);
            s = s with { Run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, s.Run.AgentRunId))! };
            if (unavailable) browser.CaptureProbe = () => new TimeoutException("controlled semantic failure");
            if (changed) browser.CaptureConsistencyProbe = p => p.EvaluateAsync("() => document.body.append(document.createElement('p'))");
            (s, var capture) = await BrowserModelApproval(f, s, browser, capture: true);
            var continuation = BrowserContinuations(f, browser);
            try { await continuation.PrepareExternalEffectAsync(s.Run); }
            finally { browser.CaptureProbe = null; browser.CaptureConsistencyProbe = null; }
            var saved = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, s.Run.AgentRunId))!;
            var resumed = (await continuation.ResumeAsync(saved)).Run; s = s with { Run = resumed };
            Assert.True(AgentRunToolCallCheckpoint.TryRead(resumed.Checkpoint, out var transcript));
            var messages = await Effects(f, browser).RehydrateCapturesAsync(resumed, transcript!.ToList(), vision);
            var message = Assert.Single(messages, m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.RequestAuthorization);
            using var result = JsonDocument.Parse(message.Text); var root = result.RootElement;
            Assert.False(root.GetProperty("observationUnavailable").GetBoolean());
            Assert.Equal(vision, root.GetProperty("captureImageProvided").GetBoolean());
            var native = root.GetProperty("browser").GetProperty("data");
            Assert.Equal(unavailable, native.GetProperty("observationUnavailable").GetBoolean());
            var coordinates = native.GetProperty("coordinateEvidence").GetBoolean();
            if (changed)
            {
                Assert.False(coordinates);
                Assert.Contains("visual_state_changed_during_capture", native.GetProperty("coordinateEvidenceUnavailableReasons").EnumerateArray().Select(v => v.GetString()));
            }
            if (coordinates)
            {
                Assert.True(native.GetProperty("captureDiagnostics").GetProperty("settlement").GetProperty("settled").GetBoolean());
                Assert.True(native.GetProperty("captureDiagnostics").GetProperty("settlement").GetProperty("observationAvailable").GetBoolean());
            }
            else Assert.NotEmpty(native.GetProperty("coordinateEvidenceUnavailableReasons").EnumerateArray());
            var receipt = (await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(s.Tenant, capture.Operation.OperationId))!;
            var artifact = Assert.Single(TenantBrowserArtifactRules.Read(receipt.BrowserArtifactsJson!).Files);
            var bytes = await ((ITenantArtifactStore)f.Artifacts).ReadTenantContentAsync(s.Tenant, s.Run.SessionId, artifact.ArtifactId);
            using var bitmap = SKBitmap.Decode(bytes); Assert.NotNull(bitmap);
            Assert.InRange((int)(box.X + box.Width / 2), 0, bitmap.Width - 1);
            Assert.InRange((int)(box.Y + box.Height / 2), 0, bitmap.Height - 1);
            var pixel = bitmap.GetPixel((int)(box.X + box.Width / 2), (int)(box.Y + box.Height / 2));
            Assert.True(pixel.Red < 32 && pixel.Green < 32 && pixel.Blue < 32);
            if (vision) Assert.Equal(bytes, Assert.Single(message.Parts!.OfType<ModelImageContent>()).Bytes.ToArray()); else Assert.Empty(message.Parts ?? []);
            Assert.DoesNotContain("protected-visible-value", message.Text); Assert.DoesNotContain(Convert.ToBase64String(bytes), resumed.Checkpoint!.PayloadJson);
            // Repeating the exact completed intent is a deduped receipt lookup.
            // A distinct capture must consume fresh allowance and is denied here.
            var exhausted = await Assert.ThrowsAsync<AccessDeniedException>(() => Request(Service(f, browser), s, ToolCatalog.BrowserScreenshot, """{"fullPage":true}""").AsTask());
            Assert.Equal("capture_limit", exhausted.ReasonCode);
            var fresh = await ReadService(f, browser).InspectAsync(Frame(s), ReadCall("fresh"), 32768);
            Assert.DoesNotContain("\"error\"", fresh);
            // Coordinate evidence at the native adapter never grants an unconverted
            // tenant command, even to an actual owner with a valid capture.
            var snapshot = root.GetProperty("browser").GetProperty("observation").GetProperty("snapshotId").GetString();
            var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => Request(Service(f, browser), s, ToolCatalog.BrowserVisionMouse,
                JsonSerializer.Serialize(new { operation = "click", x = 680, y = 165, snapshotId = snapshot })).AsTask());
            Assert.Equal("browser_resource_adapter_unavailable", denied.ReasonCode);
            Assert.Equal(receipt, (await ExecuteEffect(Effects(f, browser), s, capture)).Receipt);
            Assert.Single((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(s.Tenant, s.Run.SessionId, null, null, 100)).Items);
        }
        finally { await browser.StopAsync(default); }
    }
}
