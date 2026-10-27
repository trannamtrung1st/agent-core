using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    private static async Task<(Environment Setup, TenantRuntimeFrame Frame, TenantApprovalService Approvals, TenantApprovalRequest Request)> MutationReviewFixture(Fixture f, bool working, bool exceptional)
    {
        var s = await Setup(f, "general-assistant", 35); var frame = await ClaimArtifactRun(f, s); await SeedRuntimeWorkspace(f, s, working);
        await GrantWorkspaceRuntimeReads(f, s, exceptional ? ["agents.read", "sessions.read"] : ["agents.read", "sessions.read", "workspaces.write"]);
        if (!exceptional) await Policies(f).SaveAsync(s.Tenant, s.Owner, "workspaces.write", new(OperationApprovalMode.Always, false), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision, 0);
        var args = JsonSerializer.Serialize(new { path = (working ? "/working/" : "/home/") + "private.txt", content = "Exact reviewed literal replacement" });
        var approvals = ApprovalService(f, workspaceMutations: MutationTargets(f));
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, frame.RunId))!;
        var prepared = await approvals.PrepareWorkspaceMutationAsync(new(s.Tenant, frame.RunId), new("prepare-exact-mutation", ToolCatalog.WorkspaceWrite, args), run.Revision, frame.Generation);
        Assert.Equal(OperationDecisionKind.RequiresApproval, prepared.Decision); var request = Assert.IsType<TenantApprovalRequest>(prepared.Request);
        Assert.Equal(exceptional ? ApprovalKind.ExceptionalGrant : ApprovalKind.Review, request.Kind); Assert.Equal(TenantOperationKind.WorkspaceMutation, request.Operation.Kind);
        return (s, frame, approvals, request);
    }

    [Theory] [InlineData(false, false, false)] [InlineData(true, false, false)] [InlineData(false, true, false)] [InlineData(true, true, false)]
    [InlineData(false, false, true)] [InlineData(true, false, true)] [InlineData(false, true, true)] [InlineData(true, true, true)]
    public async Task Tenant_workspace_mutation_Review_or_exact_grant_persists_and_decides_without_applying_or_exposing_current_file_bytes(bool sqlite, bool working, bool exceptional)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await MutationReviewFixture(f, working, exceptional); var s = v.Setup;
        var waiting = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, v.Frame.RunId))!;
        Assert.Equal(AgentRunStatus.WaitingForApproval, waiting.Status); Assert.Null(waiting.Claim);
        var view = await v.Approvals.ReadViewAsync(s.Tenant, v.Request.RequestId, s.Owner); Assert.True(view.Review!.CanDecide, view.Review.DecisionUnavailableReason);
        Assert.Contains(view.Review.Changes, c => c.Field == "workspaceMutationArguments" && ((string)c.After!).Contains("Exact reviewed literal replacement"));
        Assert.DoesNotContain("Private model bytes", JsonSerializer.Serialize(view.Review));
        Assert.DoesNotContain(v.Request.Operation.WorkspaceMutation!.ObservationHash, JsonSerializer.Serialize(view.Review));
        var decided = await v.Approvals.DecideAsync(s.Tenant, v.Request.RequestId, s.Owner, true, v.Request.Operation.ActionHash, view.SecurityRevision, v.Request.Revision);
        Assert.Equal(TenantApprovalStatus.Approved, decided.Status);
        var grant = await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(s.Tenant, v.Request.RequestId);
        Assert.Equal(exceptional, grant is not null); if (grant is not null) { Assert.Equal(OperationGrantStatus.Available, grant.Status); Assert.Equal(v.Request.Operation.OperationId, grant.OperationId); }
        Assert.Single(await ((ITenantApprovalStore)f.Iam).PendingApprovalSignalsAsync(100));
        var bytes = await WorkspaceReader(f).InspectTenantAsync(WorkspaceOwner(s, working), (working ? "/working/" : "/home/") + "private.txt", 1024);
        Assert.Equal("Private model bytes: thử nghiệm", System.Text.Encoding.UTF8.GetString(bytes.Bytes));
        Assert.Null(await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(s.Tenant, v.Request.Operation.OperationId));
        Assert.DoesNotContain(await f.Iam.ReadAuditAsync(s.Tenant, 200), a => a.Action == TenantWorkspaceEffectRules.Dispatch);
    }
}
