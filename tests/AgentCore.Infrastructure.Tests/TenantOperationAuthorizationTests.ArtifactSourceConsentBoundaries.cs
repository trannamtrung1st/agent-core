using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    private sealed record SourceConsentCase(Environment Setup, TenantRuntimeFrame Frame, TenantApprovalService Approvals,
        TenantApprovalRequest Request, string Arguments, byte[] Bytes, bool Working, RoleAssignment RawReads, TenantSessionInitiator Reviewer);
    private static async Task<SourceConsentCase> SourceConsent(Fixture f, bool working = false, bool grant = false, string policy = "workspaces.read", bool independent = false, bool resource = false)
    {
        var s = await Setup(f, "general-assistant", 35); byte[] bytes = resource ? "Exact resource source body"u8.ToArray() : [0, 255, 128, 3];
        var bound = resource ? await ResourceSourceCase(f, s, bytes) : null;
        if (bound is not null) { s = bound.Setup; working = false; if (policy == "workspaces.read") policy = "resources.read"; }
        else await SeedRuntimeWorkspace(f, s, working, "source.bin", bytes);
        var frame = await ClaimArtifactRun(f, s);
        await GrantPublication(f, s, grant ? ["sessions.read"] : null);
        var reads = await GrantWorkspaceRuntimeReads(f, s, resource ? ["sessions.read", "agents.read", "resources.read"] : null);
        var reviewer = independent ? await User(f, s, "source-reviewer@example.test",
            ["sessions.read", "agents.read", resource ? "resources.read" : "workspaces.read", "artifacts.create", "approvals.read", "approvals.approve", "approvals.grant_exception"]) : s.Owner;
        await Policies(f).SaveAsync(s.Tenant, s.Owner, policy, new(OperationApprovalMode.Always, independent), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision,
            (await f.Iam.ReadTenantAsync(s.Tenant))!.OperationPolicies.SingleOrDefault(p => p.PermissionKey == policy)?.Revision ?? 0);
        var args = JsonSerializer.Serialize(new { path = bound?.Path ?? (working ? "/working/" : "/home/") + "source.bin", displayName = "published.bin" });
        var approvals = ApprovalService(f, artifactSources: ArtifactSources(f)); var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, frame.RunId))!;
        var request = (await approvals.PrepareWorkspaceArtifactAsync(new(s.Tenant, frame.RunId), args, run.Revision, frame.Generation)).Request!;
        Assert.NotNull(request); return new(s, frame, approvals, request, args, bytes, working, reads, reviewer);
    }
    private static async Task<AgentRun> ResumeSourceConsent(Fixture f, SourceConsentCase v, TimeSpan? lease = null)
    {
        var s = v.Setup; var request = v.Request;
        await v.Approvals.DecideAsync(s.Tenant, request.RequestId, v.Reviewer, true, request.Operation.ActionHash, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 1);
        Assert.Equal(1, await v.Approvals.DispatchPendingAsync()); var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, v.Frame.RunId))!;
        return await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
            new AgentRunCommand.Claim(run.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().Add(lease ?? TimeSpan.FromMinutes(5))), f.Ids.NewId());
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Artifact_source_consent_filters_raw_source_visibility_before_counts_and_scope_qualifies_reviewers(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true); var v = await SourceConsent(f); var s = v.Setup;
        var reader = await User(f, s, "source-inbox-reader@example.test", ["sessions.read", "approvals.read", "artifacts.create"]);
        async Task<RoleAssignment> Allow(string key, AccessScope scope)
        {
            var role = await f.Access.SaveRoleAsync(s.Owner.PrincipalId, s.Tenant, null, "Scoped source " + key, [key], (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
            return await f.Access.AssignRoleAsync(s.Owner.PrincipalId, s.Tenant, reader.PrincipalId, role.RoleId, scope, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        }
        async Task Hidden()
        {
            var page = await v.Approvals.ListAsync(s.Tenant, reader, new(0, 1)); Assert.Equal(0, page.Total); Assert.Empty(page.Items);
            await Assert.ThrowsAsync<AccessDeniedException>(() => v.Approvals.ReadViewAsync(s.Tenant, v.Request.RequestId, reader).AsTask());
        }
        await Hidden(); var workspace = TenantWorkspaceRules.Home(s.Tenant, SpaceId(s)); await Allow("workspaces.read", workspace); await Hidden();
        await Allow("agents.read", AccessScope.ForResource(s.Tenant, "agent", s.Agent.Instance.InstanceId.ToString("D")));
        var visible = await v.Approvals.ListAsync(s.Tenant, reader, new(0, 1)); Assert.Equal(1, visible.Total); Assert.Single(visible.Items); Assert.False(visible.Items[0].Review!.CanDecide);
        await Allow("approvals.approve", TenantSessionRules.Scope(s.Tenant, v.Frame.SessionId));
        Assert.False((await v.Approvals.ReadViewAsync(s.Tenant, v.Request.RequestId, reader)).Review!.CanDecide);
        var scoped = await Allow("approvals.approve", workspace);
        Assert.True((await v.Approvals.ReadViewAsync(s.Tenant, v.Request.RequestId, reader)).Review!.CanDecide);
        await f.Access.RevokeAssignmentAsync(s.Owner.PrincipalId, s.Tenant, scoped.AssignmentId, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        await Assert.ThrowsAsync<AccessDeniedException>(() => v.Approvals.DecideAsync(s.Tenant, v.Request.RequestId, reader, true,
            v.Request.Operation.ActionHash, (f.Iam.ReadTenantAsync(s.Tenant).Result)!.Tenant.Revision, 1).AsTask());
    }

    [Theory] [InlineData(false, "sessions.read")] [InlineData(true, "sessions.read")]
    [InlineData(false, "agents.read")] [InlineData(true, "agents.read")]
    [InlineData(false, "workspaces.read")] [InlineData(true, "workspaces.read")]
    public async Task Tenant_Artifact_source_consent_never_substitutes_a_creation_exception_for_missing_raw_source_reads(bool sqlite, string permission)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true); var s = await Setup(f, "general-assistant", 35); var frame = await ClaimArtifactRun(f, s);
        await GrantWorkspaceRuntimeReads(f, s, new[] { "sessions.read", "agents.read", "workspaces.read" }.Where(k => k != permission).ToArray());
        await SeedRuntimeWorkspace(f, s, false, "source.bin", [0, 255, 128, 3]);
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, frame.RunId))!;
        Assert.Equal("executor_read_required", (await Assert.ThrowsAsync<AccessDeniedException>(() => ApprovalService(f, artifactSources: ArtifactSources(f))
            .PrepareWorkspaceArtifactAsync(new(s.Tenant, frame.RunId), """{"path":"/home/source.bin","displayName":"published.bin"}""", run.Revision, frame.Generation).AsTask())).ReasonCode);
        Assert.Empty((await ((ITenantApprovalStore)f.Iam).ListApprovalsAsync(s.Tenant, null, null, new(0, 20))).Items);
        Assert.Empty((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(s.Tenant, frame.SessionId, null, null, 20)).Items);
    }

    [Theory] [InlineData(false, "sessions.read")] [InlineData(true, "sessions.read")]
    [InlineData(false, "agents.read")] [InlineData(true, "agents.read")]
    [InlineData(false, "workspaces.read")] [InlineData(true, "workspaces.read")]
    public async Task Tenant_Artifact_source_consent_combines_each_current_source_policy_with_primary_creation(bool sqlite, string permission)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true); var v = await SourceConsent(f, policy: permission);
        Assert.Equal(ApprovalKind.Review, v.Request.Kind); Assert.Equal("artifacts.create", v.Request.Permission);
        Assert.True((await v.Approvals.ReadViewAsync(v.Setup.Tenant, v.Request.RequestId, v.Reviewer)).Review!.CanDecide);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Artifact_source_consent_enforces_source_independence_even_with_primary_policy_Allowed(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true); var v = await SourceConsent(f, independent: true); var s = v.Setup;
        var own = await v.Approvals.ReadViewAsync(s.Tenant, v.Request.RequestId, s.Owner); Assert.True(own.Review!.IndependentReview); Assert.False(own.Review.CanDecide);
        await Assert.ThrowsAsync<AccessDeniedException>(() => v.Approvals.DecideAsync(s.Tenant, v.Request.RequestId, s.Owner, true,
            v.Request.Operation.ActionHash, (f.Iam.ReadTenantAsync(s.Tenant).Result)!.Tenant.Revision, 1).AsTask());
        Assert.True((await v.Approvals.ReadViewAsync(s.Tenant, v.Request.RequestId, v.Reviewer)).Review!.CanDecide);
    }

    [Theory] [InlineData(false, false, false)] [InlineData(true, false, false)] [InlineData(false, true, false)] [InlineData(true, true, false)]
    [InlineData(false, false, true)] [InlineData(true, false, true)] [InlineData(false, true, true)] [InlineData(true, true, true)]
    public async Task Tenant_Artifact_source_consent_refuses_changed_sources_before_decision_or_effect_without_adapting_or_replaying(bool sqlite, bool grant, bool afterDecision)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true); var v = await SourceConsent(f, working: true, grant: grant); var s = v.Setup;
        AgentRun? run = afterDecision ? await ResumeSourceConsent(f, v) : null;
        await SeedRuntimeWorkspace(f, s, true, "source.bin", [0, 255, 128, 4]);
        if (run is null)
        {
            var view = await v.Approvals.ReadViewAsync(s.Tenant, v.Request.RequestId, s.Owner); Assert.False(view.Review!.CanDecide); Assert.True(view.Review.CanCancel);
            Assert.Equal("workspace_source_changed", view.Review.DecisionUnavailableReason);
            Assert.Equal("workspace_source_changed", (await Assert.ThrowsAsync<SecurityConflictException>(() => v.Approvals.DecideAsync(s.Tenant, v.Request.RequestId, s.Owner, true,
                v.Request.Operation.ActionHash, (f.Iam.ReadTenantAsync(s.Tenant).Result)!.Tenant.Revision, 1).AsTask())).ReasonCode);
            await v.Approvals.CancelAsync(s.Tenant, v.Request.RequestId, s.Owner, v.Request.Operation.ActionHash, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 1);
        }
        else
        {
            var effects = Effects(f, artifactSources: ArtifactSources(f));
            var receipt = await effects.ExecuteAsync(new(s.Tenant, run.AgentRunId), v.Request.RequestId, v.Request.Operation.OperationId, run.Claim!.Generation, v.Request.Operation.ActionHash);
            Assert.Equal(TenantOperationOutcome.Denied, receipt.Outcome); Assert.Equal("workspace_source_changed", receipt.ReasonCode);
            await SeedRuntimeWorkspace(f, s, true, "source.bin", v.Bytes);
            Assert.Equal(receipt, await effects.ExecuteAsync(new(s.Tenant, run.AgentRunId), v.Request.RequestId, v.Request.Operation.OperationId, run.Claim.Generation, v.Request.Operation.ActionHash));
            var settled = await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(s.Tenant, v.Request.RequestId);
            if (grant) Assert.Equal(OperationGrantStatus.Revoked, settled!.Status); else Assert.Null(settled);
        }
        Assert.Empty((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(s.Tenant, v.Frame.SessionId, null, null, 20)).Items);
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_Artifact_source_consent_rechecks_executor_and_grantor_authority_at_effect(bool sqlite, bool grantor)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true); var v = await SourceConsent(f, grant: true, independent: true); var s = v.Setup;
        var run = await ResumeSourceConsent(f, v);
        var assignment = grantor ? (await f.Iam.ReadTenantAsync(s.Tenant))!.Assignments.Single(a => a.PrincipalId == v.Reviewer.PrincipalId) : v.RawReads;
        await f.Access.RevokeAssignmentAsync(s.Owner.PrincipalId, s.Tenant, assignment.AssignmentId, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        var receipt = await Effects(f, artifactSources: ArtifactSources(f)).ExecuteAsync(new(s.Tenant, run.AgentRunId), v.Request.RequestId, v.Request.Operation.OperationId, run.Claim!.Generation, v.Request.Operation.ActionHash);
        Assert.Equal(TenantOperationOutcome.Denied, receipt.Outcome); Assert.Equal(grantor ? "grantor_authority_lost" : "executor_read_required", receipt.ReasonCode);
        Assert.Equal(OperationGrantStatus.Revoked, (await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(s.Tenant, v.Request.RequestId))!.Status);
        Assert.Empty((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(s.Tenant, v.Frame.SessionId, null, null, 20)).Items);
    }
}
