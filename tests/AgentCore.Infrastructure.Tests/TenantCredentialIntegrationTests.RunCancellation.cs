using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    [Theory]
    [InlineData(false, "pending")] [InlineData(true, "pending")]
    [InlineData(false, "approved")] [InlineData(true, "approved")]
    [InlineData(false, "expired")] [InlineData(true, "expired")]
    public async Task Run_cancellation_retires_only_pending_consent_or_available_grant_without_delegating_approval_authority(bool sqlite, string state)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-17T14:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock); var setup = await RuntimeCredential(f, false); var approvals = RuntimeApprovals(f);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var request = (await approvals.PrepareCredentialCheckAsync(new(setup.Tenant, setup.Run.AgentRunId), "work-drive", setup.Run.Revision, setup.Run.Claim!.Generation)).Request!;
        if (state == "approved")
        {
            var view = await approvals.ReadViewAsync(setup.Tenant, request.RequestId, setup.Owner);
            request = await approvals.DecideAsync(setup.Tenant, request.RequestId, setup.Owner, true, request.Operation.ActionHash, view.SecurityRevision, request.Revision);
        }
        if (state == "expired") clock.Advance(TimeSpan.FromHours(2));
        var account = await f.Identity.RegisterAsync("stopper@example.test", "Stopper", "strong-stopper-password");
        var role = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Stop only",
            ["agent-runs.read", "agent-runs.cancel", "sessions.read", "agents.chat"], await Revision());
        var invite = await f.Access.InviteAsync(setup.Owner.PrincipalId, setup.Tenant, account.LoginKey, role.RoleId, await Revision());
        var member = await f.Access.AcceptInvitationAsync(account.AccountId, setup.Tenant, invite.Invitation.InvitationId, invite.Token);
        var login = (await f.Identity.SignInAsync(account.LoginKey, "strong-stopper-password"))!.Session;
        var human = new TenantSessionInitiator(account.AccountId, login.SessionId, member.PrincipalId);
        foreach (var permission in new[] { "approvals.cancel", "approvals.approve", "approvals.grant_exception", "credentials.use" })
            Assert.False((await f.Authorizer.CheckAsync(human.PrincipalId, permission,
                permission.StartsWith("credentials", StringComparison.Ordinal) ? AccessScope.ForResource(setup.Tenant, "credential", setup.CredentialId.ToString("D"))
                    : AccessScope.ForResource(setup.Tenant, "approval", request.RequestId.ToString("D")))).Allowed);
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, setup.Run.AgentRunId))!;
        var result = await f.RunService().CancelAsync(setup.Tenant, run.AgentRunId, human, await Revision(), run.Revision);
        Assert.Equal(AgentRunStatus.Cancelled, result.Status); Assert.Equal(setup.Owner.PrincipalId, result.Context.OriginalInitiatorPrincipalId);
        var closed = (await ((ITenantApprovalStore)f.Iam).FindApprovalAsync(setup.Tenant, request.RequestId))!;
        Assert.Equal(state == "approved" ? TenantApprovalStatus.Approved : state == "expired" ? TenantApprovalStatus.Expired : TenantApprovalStatus.Cancelled, closed.Status);
        var decision = await ((ITenantApprovedOperationStore)f.Iam).FindApprovalDecisionAsync(setup.Tenant, request.RequestId);
        if (state == "expired") Assert.Null(decision);
        else Assert.Equal(state == "approved" ? setup.Owner.PrincipalId : human.PrincipalId, decision!.HumanPrincipalId);
        var grant = await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(setup.Tenant, request.RequestId);
        if (state == "approved") Assert.Equal(OperationGrantStatus.Revoked, grant!.Status); else Assert.Null(grant);
        await approvals.DispatchPendingAsync();
        Assert.Single(await ((ITenantRunStore)f.Iam).ListTenantRunIdsAsync(setup.Tenant, run.SessionId));
        Assert.Equal(AgentRunStatus.Cancelled, (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, run.AgentRunId))!.Status);
        Assert.Null(await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(setup.Tenant, request.Operation.OperationId));
        var audit = Assert.Single(await f.Iam.ReadAuditAsync(setup.Tenant, 100), a => a.Action == "agent-runs.cancel");
        Assert.Equal(human.PrincipalId, audit.ActorPrincipalId);
        Assert.DoesNotContain(await f.Iam.ReadAuditAsync(setup.Tenant, 100), a => a.Action == TenantCredentialCheckRules.Action);
    }
}
