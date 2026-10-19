using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Security;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantBrowserApprovalTests
{
    private static readonly byte[] CapturePng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a6XcAAAAASUVORK5CYII=");
    private static async Task<RoleAssignment> GrantBrowserPublication(Fixture f, Setup s, bool read = false)
    {
        var role = await f.Access.SaveRoleAsync(s.Human.PrincipalId, s.Tenant, null, "Browser file publication",
            read ? ["artifacts.create", "artifacts.read"] : ["artifacts.create"], (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        return await f.Access.AssignRoleAsync(s.Human.PrincipalId, s.Tenant, s.Agent.PrincipalId, role.RoleId,
            read ? AccessScope.ForTenant(s.Tenant) : TenantSessionRules.Scope(s.Tenant, s.Run.SessionId), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Browser_capture_publishes_atomic_owned_Artifact_and_body_free_receipt_then_never_replays(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true);
        var s = await Prepare(f); await GrantBrowserPublication(f, s);
        var probe = new EffectProbe(Probe(s)) { Result = new(null, Bytes: CapturePng, ContentType: "image/png", FileName: "screenshot.png", DataJson: "{\"coordinateEvidence\":false}") };
        (s, var request) = await ApprovedEffect(f, s, probe, tool: ToolCatalog.BrowserScreenshot, args: "{}");
        Assert.Equal("browser.use", request.Permission); Assert.True(request.Operation.BrowserCommand!.Artifacts!.Capture);
        Assert.Equal("Protected", request.Operation.BrowserCommand.Artifacts.CapturePrivacyMode);
        var effect = await ExecuteEffect(Effects(f, probe), s, request);
        Assert.Equal(TenantOperationOutcome.Completed, effect.Receipt.Outcome);
        var publication = TenantBrowserArtifactRules.Read(Assert.IsType<string>(effect.Receipt.BrowserArtifactsJson));
        Assert.Equal("published", publication.Status); Assert.Equal(request.Operation.OperationId, Assert.Single(publication.Files).ArtifactId);
        var file = await ((ITenantArtifactStore)f.Artifacts).FindTenantAsync(s.Tenant, s.Run.SessionId, request.Operation.OperationId);
        Assert.NotNull(file); Assert.Equal(s.Run.AgentRunId, file.AgentRunId); Assert.Equal(s.Tenant, file.TenantId);
        Assert.Equal(CapturePng, await ((ITenantArtifactStore)f.Artifacts).ReadTenantContentAsync(s.Tenant, s.Run.SessionId, file.ArtifactId));
        Assert.Null(effect.Result!.Bytes); Assert.Contains("artifactPublication", effect.Result.DataJson);
        Assert.DoesNotContain(Convert.ToBase64String(CapturePng), JsonSerializer.Serialize(effect.Receipt));
        IIamStore reopened = sqlite ? new SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns, clock: f.Clock) : f.Iam;
        Assert.Equal(effect.Receipt, (await ExecuteEffect(Effects(f, probe, reopened), s, request)).Receipt); Assert.Equal(1, probe.NativeEffects);
        Assert.Single((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(s.Tenant, s.Run.SessionId, null, null, 100)).Items);
    }
}
