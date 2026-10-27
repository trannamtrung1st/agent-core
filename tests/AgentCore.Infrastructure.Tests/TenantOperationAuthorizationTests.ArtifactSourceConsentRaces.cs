using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.Extensions.Time.Testing;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    [Theory] [InlineData(false, "lease")] [InlineData(true, "lease")]
    [InlineData(false, "grant")] [InlineData(true, "grant")] [InlineData(false, "cancel")] [InlineData(true, "cancel")]
    public async Task Tenant_Artifact_source_consent_materialization_repeats_claim_expiry_and_cancellation_before_effect_commit(bool sqlite, string boundary)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddSeconds(1));
        await using var f = await Fixture.Create(sqlite, clock, builtInAuthority: true, artifactAuthority: true); var v = await SourceConsent(f, grant: true); var s = v.Setup;
        var run = await ResumeSourceConsent(f, v, boundary == "grant" ? TimeSpan.FromHours(2) : null); using var cancellation = new CancellationTokenSource();
        var probe = new ArtifactSourceProbe(WorkspaceReader(f), beforeCurrent: call =>
        {
            if (call == 2) { if (boundary == "cancel") cancellation.Cancel(); else clock.Advance(TimeSpan.FromMinutes(boundary == "grant" ? 61 : 6)); }
            return ValueTask.CompletedTask;
        });
        var effects = Effects(f, artifactSources: ArtifactSources(f, probe));
        async Task<TenantOperationReceipt> Execute() => await effects.ExecuteAsync(new(s.Tenant, run.AgentRunId), v.Request.RequestId, v.Request.Operation.OperationId,
            run.Claim!.Generation, v.Request.Operation.ActionHash, cancellation.Token);
        if (boundary == "grant")
        {
            var receipt = await Execute(); Assert.Equal(TenantOperationOutcome.Denied, receipt.Outcome); Assert.Equal("grant_expired", receipt.ReasonCode);
            Assert.Equal(OperationGrantStatus.Revoked, (await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(s.Tenant, v.Request.RequestId))!.Status);
        }
        else
        {
            if (boundary == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(Execute);
            else Assert.Equal("execution_claim_expired", (await Assert.ThrowsAsync<SecurityConflictException>(Execute)).ReasonCode);
            Assert.Null(await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(s.Tenant, v.Request.Operation.OperationId));
            Assert.Equal(OperationGrantStatus.Available, (await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(s.Tenant, v.Request.RequestId))!.Status);
        }
        Assert.Empty((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(s.Tenant, v.Frame.SessionId, null, null, 20)).Items);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Artifact_source_consent_preparation_rechecks_raw_reads_after_source_IO(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true); var s = await Setup(f, "general-assistant", 35); var frame = await ClaimArtifactRun(f, s);
        await GrantPublication(f, s); var raw = await GrantWorkspaceRuntimeReads(f, s); await SeedRuntimeWorkspace(f, s, false, "source.bin", [0, 255, 128, 3]);
        var probe = new ArtifactSourceProbe(WorkspaceReader(f), afterCapture: async () => await f.Access.RevokeAssignmentAsync(s.Owner.PrincipalId, s.Tenant, raw.AssignmentId,
            (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision));
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, frame.RunId))!;
        Assert.Equal("executor_read_required", (await Assert.ThrowsAsync<AccessDeniedException>(() => ApprovalService(f, artifactSources: ArtifactSources(f, probe))
            .PrepareWorkspaceArtifactAsync(new(s.Tenant, frame.RunId), """{"path":"/home/source.bin","displayName":"published.bin"}""", run.Revision, frame.Generation).AsTask())).ReasonCode);
        Assert.Empty((await ((ITenantApprovalStore)f.Iam).ListApprovalsAsync(s.Tenant, null, null, new(0, 20))).Items);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Artifact_source_consent_working_visibility_needs_independent_Session_and_workspace_reads_without_home_Agent_read(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true); var v = await SourceConsent(f, working: true); var s = v.Setup;
        var reader = await User(f, s, "working-source-reader@example.test", ["sessions.read", "workspaces.read", "artifacts.create", "approvals.read"]);
        Assert.False((await f.Authorizer.CheckAsync(reader.PrincipalId, "agents.read", AccessScope.ForResource(s.Tenant, "agent", s.Agent.Instance.InstanceId.ToString("D")))).Allowed);
        var page = await v.Approvals.ListAsync(s.Tenant, reader, new(0, 1)); Assert.Equal(1, page.Total); Assert.Single(page.Items);
        var view = await v.Approvals.ReadViewAsync(s.Tenant, v.Request.RequestId, reader); Assert.False(view.Review!.CanDecide);
        Assert.Contains(view.Review.Changes, c => c.Field == "artifactSourcePath" && Equals(c.After, "/working/source.bin"));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Artifact_source_consent_uses_original_requester_for_source_policy_even_when_creation_is_Allowed(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true); var s = await Setup(f, "general-assistant", 35);
        var requester = await User(f, s, "requester-source-policy@example.test", ["agents.read", "agents.chat", "sessions.create", "sessions.read", "artifacts.create"]);
        var session = await f.Sessions.CreateAsync(s.Tenant, s.Agent.Instance.InstanceId, requester, SessionMode.Text, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, s.Agent.Instance.Revision);
        var submitted = await f.RunService().SubmitTextAsync(s.Tenant, session.Owner.SessionId, requester, f.Ids.NewId(), "Publish the protected source", (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 1);
        s = s with { Run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, submitted.RunId))! }; var frame = await ClaimArtifactRun(f, s);
        await GrantPublication(f, s); await GrantWorkspaceRuntimeReads(f, s); await SeedRuntimeWorkspace(f, s, false, "source.bin", [0, 255, 128, 3]);
        await Policies(f).SaveAsync(s.Tenant, s.Owner, "workspaces.read", new(OperationApprovalMode.WhenRequesterLacksPermission, false), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 0);
        const string args = """{"path":"/home/source.bin","displayName":"published.bin"}""";
        Assert.Equal("artifact_source_review_required", (await Assert.ThrowsAsync<AccessDeniedException>(() => SourcePublicationService(f).ExecuteWorkspaceAsync(new(s.Tenant, frame.RunId), frame.Generation, "requester-source", args).AsTask())).ReasonCode);
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, frame.RunId))!;
        var approvals = ApprovalService(f, artifactSources: ArtifactSources(f));
        var prepared = await approvals.PrepareWorkspaceArtifactAsync(new(s.Tenant, frame.RunId), args, run.Revision, frame.Generation);
        Assert.Equal(OperationDecisionKind.RequiresApproval, prepared.Decision); Assert.Equal(ApprovalKind.Review, prepared.Request!.Kind);
        Assert.Equal(requester.PrincipalId, prepared.Request.OriginalInitiatorPrincipalId); Assert.True((await approvals.ReadViewAsync(s.Tenant, prepared.Request.RequestId, s.Owner)).Review!.CanDecide);
        Assert.Empty((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(s.Tenant, frame.SessionId, null, null, 20)).Items);
    }
}
