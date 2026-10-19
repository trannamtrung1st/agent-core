using System.Security.Cryptography;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Execution;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Office;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    private static TenantArtifactSourceTarget ArtifactSources(Fixture f, ITenantWorkspaceArtifactSourceStore? files = null) =>
        new(Operations(f), (ITenantSessionStore)f.Iam, f.RunConfiguration(), files ?? WorkspaceReader(f), f.Authorizer, f.Clock, ResourceSources(f), (ITenantAttachmentStore)f.Attachments);

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_Artifact_source_consent_copies_only_the_saved_package_under_compound_source_Review(bool sqlite, bool grant)
        => await RunSourcePublicationConsentCase(sqlite, grant, false);

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public Task Tenant_resource_Artifact_consent_preserves_exact_source_and_creation_only_grant(bool sqlite, bool grant) => RunSourcePublicationConsentCase(sqlite, grant, true);

    private async Task RunSourcePublicationConsentCase(bool sqlite, bool grant, bool resource)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true); var s = await Setup(f, "general-assistant", 32);
        var bytes = new OfficeProcessor().CreateDocument(new("Exact report", [new(null, ["Private saved package body"], null)]), default).Bytes;
        ResourceCase? bound = resource ? await ResourceSourceCase(f, s, bytes, "source.docx", OfficeLimits.DocumentMime) : null;
        if (bound is not null) s = bound.Setup; else await SeedRuntimeWorkspace(f, s, true, "source.docx", bytes, OfficeLimits.MaxPackageBytes);
        var frame = await ClaimArtifactRun(f, s); await GrantPublication(f, s, grant ? ["sessions.read"] : null);
        await GrantWorkspaceRuntimeReads(f, s, resource ? ["sessions.read", "agents.read", "resources.read"] : null);
        await Policies(f).SaveAsync(s.Tenant, s.Owner, resource ? "resources.read" : "workspaces.read", new(OperationApprovalMode.Always, false), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 0);
        var approvals = ApprovalService(f, artifactSources: ArtifactSources(f)); var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, frame.RunId))!;
        var request = (await approvals.PrepareWorkspaceArtifactAsync(new(s.Tenant, frame.RunId), JsonSerializer.Serialize(new { path = bound?.Path ?? "/working/source.docx", displayName = "reviewed.docx" }), run.Revision, frame.Generation)).Request!;
        Assert.NotNull(request); Assert.Equal(grant ? ApprovalKind.ExceptionalGrant : ApprovalKind.Review, request.Kind); Assert.Equal("artifacts.create", request.Permission);
        Assert.Equal(frame.SessionId.ToString("D"), request.ResourceId); Assert.NotNull(request.Operation.ArtifactPublication!.Source);
        if (resource) Assert.Equal(bound!.Key, request.Operation.ArtifactPublication.Source.ResourceKey);
        var view = await approvals.ReadViewAsync(s.Tenant, request.RequestId, s.Owner);
        Assert.True(view.Review!.CanDecide); Assert.DoesNotContain("Private saved package body", JsonSerializer.Serialize(view));
        Assert.DoesNotContain(request.Operation.ArtifactPublication.Source.ObservationHash, JsonSerializer.Serialize(view.Review));
        Assert.Empty((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(s.Tenant, frame.SessionId, null, null, 20)).Items);
        await approvals.DecideAsync(s.Tenant, request.RequestId, s.Owner, true, request.Operation.ActionHash, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 1);
        Assert.Equal(1, await approvals.DispatchPendingAsync()); run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, frame.RunId))!;
        var generation = f.Ids.NewId(); run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
            new AgentRunCommand.Claim(run.Revision, f.Clock.GetUtcNow(), generation, f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        var effects = Effects(f, artifactSources: ArtifactSources(f));
        var receipt = await effects.ExecuteAsync(new(s.Tenant, run.AgentRunId), request.RequestId, request.Operation.OperationId, generation, request.Operation.ActionHash);
        Assert.Equal(TenantOperationOutcome.Completed, receipt.Outcome);
        var artifact = Assert.Single((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(s.Tenant, frame.SessionId, null, null, 20)).Items);
        Assert.Equal(request.Operation.OperationId, artifact.ArtifactId); Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), artifact.Sha256Hex);
        Assert.Equal(bytes, await ((ITenantArtifactStore)f.Artifacts).ReadTenantContentAsync(s.Tenant, frame.SessionId, artifact.ArtifactId));
        Assert.Equal(receipt, await effects.ExecuteAsync(new(s.Tenant, run.AgentRunId), request.RequestId, request.Operation.OperationId, generation, request.Operation.ActionHash));
        var operationGrant = await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(s.Tenant, request.RequestId);
        if (grant) Assert.Equal(OperationGrantStatus.Consumed, operationGrant!.Status); else Assert.Null(operationGrant);
        Assert.Equal(!grant, (await f.Authorizer.CheckAsync(s.Agent.PrincipalId, "artifacts.create", TenantSessionRules.Scope(s.Tenant, frame.SessionId))).Allowed);
        Assert.False((await f.Authorizer.CheckAsync(s.Agent.PrincipalId, "artifacts.read", TenantArtifactRules.Scope(s.Tenant, artifact.ArtifactId))).Allowed);
    }
}
