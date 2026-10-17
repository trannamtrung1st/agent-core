using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Security;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Stricter_policy_persists_and_independent_review_requires_a_current_human_with_underlying_permission(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var policy = Policies(f); var operations = Operations(f); var reference = new TenantRunReference(setup.Tenant, setup.Run.AgentRunId);
        var target = setup.Agent.Instance.InstanceId.ToString("D");
        await Grant(f, setup, setup.Agent.PrincipalId, "Executor read", ["agents.read"]);
        Assert.Equal(OperationDecisionKind.Allowed, (await operations.CheckAsync(reference, "agents.read", "agent", target)).Decision);
        var configured = await policy.SaveAsync(setup.Tenant, setup.Owner, "agents.read", new(OperationApprovalMode.Always, true), await Revision(), 0);
        Assert.Equal(1, configured.PolicyRevision);
        Assert.Equal("no_eligible_approver", (await operations.CheckAsync(reference, "agents.read", "agent", target)).ReasonCode);
        var reviewer = await User(f, setup, "reviewer@example.test", ["approvals.approve"]);
        Assert.Equal("no_eligible_approver", (await operations.CheckAsync(reference, "agents.read", "agent", target)).ReasonCode);
        var grant = await Grant(f, setup, reviewer.PrincipalId, "Reviewer exact read", ["agents.read"]);
        var decision = await operations.CheckAsync(reference, "agents.read", "agent", target);
        Assert.Equal(OperationDecisionKind.RequiresApproval, decision.Decision); Assert.Equal(ApprovalKind.Review, decision.ApprovalKind);
        await f.Access.RevokeAssignmentAsync(setup.Owner.PrincipalId, setup.Tenant, grant.AssignmentId, await Revision());
        Assert.Equal("no_eligible_approver", (await operations.CheckAsync(reference, "agents.read", "agent", target)).ReasonCode);
        var witness = await Revision();
        await Assert.ThrowsAsync<ArgumentException>(() => policy.SaveAsync(setup.Tenant, setup.Owner, "agents.update", new(OperationApprovalMode.None, false), witness, 0).AsTask());
        Assert.Equal(witness, await Revision());
        await f.Agents.CreateAsync(setup.Owner.PrincipalId, setup.Tenant, "examiner", 1, await Revision());
        Assert.Equal(OperationApprovalMode.Always, (await policy.ReadAsync(setup.Tenant, setup.Owner, "agents.read")).Effective.Mode);
        if (sqlite)
        {
            var saved = (await new SqliteIamStore(f.Contexts!).ReadTenantAsync(setup.Tenant))!.OperationPolicies.Single();
            Assert.Equal(OperationApprovalMode.Always, saved.Mode); Assert.True(saved.IndependentReview);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Actual_requester_action_authority_changes_review_policy_but_never_supplies_executor_authority(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var requester = await User(f, setup, "requester@example.test", ["agents.chat", "sessions.read"]);
        var receipt = await f.RunService().SubmitTextAsync(setup.Tenant, setup.Run.SessionId, requester, f.Ids.NewId(), "New request", await Revision(), 2);
        var reference = new TenantRunReference(setup.Tenant, receipt.RunId); var target = setup.Agent.Instance.InstanceId.ToString("D"); var operations = Operations(f);
        var executor = await Grant(f, setup, setup.Agent.PrincipalId, "Executor action", ["agents.read", "agents.update"]);
        Assert.Equal(OperationDecisionKind.Allowed, (await operations.CheckAsync(reference, "agents.read", "agent", target)).Decision);
        var review = await operations.CheckAsync(reference, "agents.update", "agent", target);
        Assert.Equal(ApprovalKind.Review, review.ApprovalKind); Assert.Equal(OperationDecisionKind.RequiresApproval, review.Decision);
        await Grant(f, setup, requester.PrincipalId, "Requester action", ["agents.update"]);
        Assert.Equal(OperationDecisionKind.Allowed, (await operations.CheckAsync(reference, "agents.update", "agent", target)).Decision);
        await Policies(f).SaveAsync(setup.Tenant, setup.Owner, "agents.update", new(OperationApprovalMode.Always, false), await Revision(), 0);
        Assert.Equal(ApprovalKind.Review, (await operations.CheckAsync(reference, "agents.update", "agent", target)).ApprovalKind);
        await f.Access.RevokeAssignmentAsync(setup.Owner.PrincipalId, setup.Tenant, executor.AssignmentId, await Revision());
        var exceptional = await operations.CheckAsync(reference, "agents.update", "agent", target);
        Assert.Equal(ApprovalKind.ExceptionalGrant, exceptional.ApprovalKind); Assert.Equal(OperationDecisionKind.RequiresApproval, exceptional.Decision);
        Assert.Empty(await operations.EffectiveAsync(reference, "agent", target));
        var user = (await f.Iam.ReadTenantAsync(setup.Tenant))!.Users.Single(u => u.PrincipalId == requester.PrincipalId);
        await f.Access.SetMemberStatusAsync(setup.Owner.PrincipalId, setup.Tenant, user.UserId, IdentityStatus.Suspended, await Revision());
        Assert.Equal("requester_unavailable", (await Assert.ThrowsAsync<AccessDeniedException>(() => operations.CheckAsync(reference, "agents.read", "agent", target).AsTask())).ReasonCode);
    }

    private static TenantOperationPolicyService Policies(Fixture f, ITenantOperationPolicyStore? store = null) => new(f.Iam, store ?? (ITenantOperationPolicyStore)f.Iam, f.Authorizer, f.Identity, f.Ids, f.Clock);
    private static TenantOperationAuthorizer Operations(Fixture f) => new(f.Iam, (ITenantRunStore)f.Iam, (ITenantAgentStore)f.Iam, f.Authorizer, new((ITenantAuthorizationAuditStore)f.Iam, f.Ids, f.Clock));
    private static async Task<TenantSessionInitiator> User(Fixture f, Environment setup, string login, string[] permissions)
    {
        var account = await f.Identity.RegisterAsync(login, "User", "strong-operation-password");
        var role = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, login, permissions, (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        var invitation = await f.Access.InviteAsync(setup.Owner.PrincipalId, setup.Tenant, login, role.RoleId, (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        var user = await f.Access.AcceptInvitationAsync(account.AccountId, setup.Tenant, invitation.Invitation.InvitationId, invitation.Token);
        var session = (await f.Identity.SignInAsync(login, "strong-operation-password"))!.Session;
        return new(account.AccountId, session.SessionId, user.PrincipalId);
    }
}
