using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Tests;
public sealed partial class TenantDefinitionIntegrationTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_continuity_combined_Review_binds_both_source_owners_and_returns_current_facts_after_resumption(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var owner = await MemoryOwner(f, operations: true); var source = await MemoryClaim(f, owner); await MemoryCommit(f, source, await MemoryPlan(f, source, Learned()));
        var current = await MemoryClaim(f, owner); await MemoryPolicy(f, owner, "memory.read", OperationApprovalMode.Always); var s = ContinuityServices(f);
        var call = new ModelToolCall("search", ToolCatalog.ContinuitySearch, "{\"query\":\"Evidence\",\"limit\":10}");
        Assert.Contains("requiresApproval", await s.Reads.InspectAsync(current.Frame, call, 6000)); var request = await PrepareContinuityReview(f, current.Frame, call, s.Approvals); Assert.NotNull(request.Operation.ContinuityRead!.Memory);
        var visible = await s.Approvals.ListAsync(owner.Tenant, new(owner.Human.AccountId, owner.Human.AuthenticationSessionId, owner.Human.PrincipalId), new(0, 1)); Assert.Equal(request.RequestId, Assert.Single(visible.Items).Request.RequestId);
        var frame = await DecideMemoryReview(f, owner, request, s.Approvals); var resumed = await s.Continuation.ResumeAsync((await ((ITenantRunStore)f.Iam).FindTenantRunAsync(owner.Tenant, frame.RunId))!); Assert.Equal(TenantOperationOutcome.Completed, resumed.Outcome!.Outcome);
        Assert.True(AgentRunToolCallCheckpoint.TryRead(resumed.Run.Checkpoint, out var messages)); var body = Assert.Single(messages!, m => m.Role == ModelRole.Tool).Text;
        Assert.Contains("Use typed durable evidence", body); Assert.Contains("Evidence response completed.", body); Assert.Single(TenantHistoricalReferences.Read(resumed.Run.Checkpoint)); Assert.NotEmpty(TenantMemoryOperationReferences.Read(resumed.Run.Checkpoint));
        await ((ITenantLearnedMemoryStore)f.Iam).RequireTenantMemorySourcesAsync(new(owner.Tenant, frame.RunId), TenantMemoryOperationReferences.Read(resumed.Run.Checkpoint), frame.Generation);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_continuity_independent_read_Review_requires_a_different_current_raw_source_reader(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var owner = await MemoryOwner(f, operations: true); var source = await MemoryClaim(f, owner); await MemoryCommit(f, source, await MemoryPlan(f, source)); var current = await MemoryClaim(f, owner);
        await MemoryPolicy(f, owner, "sessions.read", OperationApprovalMode.Always, true); var s = ContinuityServices(f); var call = HistoryCall(source.Frame.SessionId);
        var denied = await s.Approvals.PrepareContinuityAsync(new(owner.Tenant, current.Frame.RunId), call, current.Run.Revision, current.Frame.Generation); Assert.Equal("no_eligible_approver", denied.ReasonCode);
        var account = await f.Identity.RegisterAsync("continuity-reviewer@example.test", "Reviewer", "strong-reviewer-password");
        var role = await f.Access.SaveRoleAsync(owner.Human.PrincipalId, owner.Tenant, null, "Historical reviewer", ["agents.read", "agents.chat", "sessions.read", "approvals.read", "approvals.approve"], (await f.Iam.ReadTenantAsync(owner.Tenant))!.Tenant.Revision);
        var invite = await f.Access.InviteAsync(owner.Human.PrincipalId, owner.Tenant, account.LoginKey, role.RoleId, (await f.Iam.ReadTenantAsync(owner.Tenant))!.Tenant.Revision); var peer = await f.Access.AcceptInvitationAsync(account.AccountId, owner.Tenant, invite.Invitation.InvitationId, invite.Token);
        var login = (await f.Identity.SignInAsync(account.LoginKey, "strong-reviewer-password"))!.Session; var actor = new TenantSessionInitiator(account.AccountId, login.SessionId, peer.PrincipalId);
        var request = await PrepareContinuityReview(f, current.Frame, call, s.Approvals); var view = await s.Approvals.ReadViewAsync(owner.Tenant, request.RequestId, actor); Assert.True(view.Review!.CanDecide, view.Review.DecisionUnavailableReason); Assert.True(view.Review.IndependentReview);
        Assert.False((await s.Approvals.ReadViewAsync(owner.Tenant, request.RequestId, new(owner.Human.AccountId, owner.Human.AuthenticationSessionId, owner.Human.PrincipalId))).Review!.CanDecide);
        Assert.Equal(request.RequestId, Assert.Single((await s.Approvals.ListAsync(owner.Tenant, actor, new(0, 1))).Items).Request.RequestId);
        await s.Approvals.DecideAsync(owner.Tenant, request.RequestId, actor, true, request.Operation.ActionHash, view.SecurityRevision, request.Revision); Assert.Equal(TenantApprovalStatus.Approved, (await ((ITenantApprovalStore)f.Iam).FindApprovalAsync(owner.Tenant, request.RequestId))!.Status);
        // Revoking the peer's raw source scope hides the exact source observation before paging.
        await f.Access.SaveRoleAsync(owner.Human.PrincipalId, owner.Tenant, role.RoleId, "Historical reviewer", ["agents.read", "agents.chat", "approvals.read", "approvals.approve"], (await f.Iam.ReadTenantAsync(owner.Tenant))!.Tenant.Revision);
        Assert.Empty((await s.Approvals.ListAsync(owner.Tenant, actor, new(0, 1))).Items);
        await Assert.ThrowsAsync<AccessDeniedException>(() => s.Approvals.ReadViewAsync(owner.Tenant, request.RequestId, actor).AsTask());
    }
}
