using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Conversation_approval_status_is_minimal_scoped_and_does_not_grant_action_inspection(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var approval = (await ApprovalService(f).PreparePersonaAsync(new(setup.Tenant, setup.Run.AgentRunId),
            setup.Agent.Instance.Persona with { Tone = "Protected proposed tone" }, setup.Run.Revision)).Request!;
        var account = await f.Identity.RegisterAsync("status-reader@example.test", "Status reader", "strong-status-password");
        var role = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Conversation only", ["sessions.read", "agents.chat"], await Revision());
        var invite = await f.Access.InviteAsync(setup.Owner.PrincipalId, setup.Tenant, account.LoginKey, role.RoleId, await Revision());
        var member = await f.Access.AcceptInvitationAsync(account.AccountId, setup.Tenant, invite.Invitation.InvitationId, invite.Token);
        var login = (await f.Identity.SignInAsync(account.LoginKey, "strong-status-password"))!.Session;
        var human = new TenantSessionInitiator(account.AccountId, login.SessionId, member.PrincipalId);
        var conversations = new TenantConversationService(f.Iam, (ITenantSessionStore)f.Iam, (ITenantConversationStore)f.Iam, f.Identity, f.Authorizer);
        var service = new TenantConversationApprovalService(conversations, ApprovalService(f), (ITenantApprovalStore)f.Iam, (ITenantRunStore)f.Iam, (ITenantApprovedOperationStore)f.Iam);
        var page = await service.ListAsync(setup.Tenant, setup.Run.SessionId, human, pageSize: 1);
        var item = Assert.Single(page.Items); Assert.Equal(1, page.Total); Assert.True(item.Waiting); Assert.False(item.CanInspect);
        Assert.Equal(approval.RequestId, item.RequestId); Assert.Equal(setup.Run.ResponseId, item.ResponseId); Assert.Null(item.Outcome);
        Assert.DoesNotContain("Protected proposed tone", System.Text.Json.JsonSerializer.Serialize(item));
        await Assert.ThrowsAsync<AccessDeniedException>(() => ApprovalService(f).ReadViewAsync(setup.Tenant, item.RequestId, human).AsTask());
        Assert.True(Assert.Single((await service.ListAsync(setup.Tenant, setup.Run.SessionId, setup.Owner)).Items).CanInspect);
        Assert.Empty((await service.ListAsync(setup.Tenant, setup.Run.SessionId, human, page: 1, pageSize: 1)).Items);
        var other = await f.Sessions.CreateAsync(setup.Tenant, setup.Agent.Instance.InstanceId, setup.Owner, SessionMode.Text, await Revision(), 1);
        Assert.Equal(0, (await service.ListAsync(setup.Tenant, other.Owner.SessionId, human)).Total);
        await ApprovalService(f).DecideAsync(setup.Tenant, item.RequestId, setup.Owner, false, approval.Operation.ActionHash, await Revision(), 1);
        var rejected = Assert.Single((await service.ListAsync(setup.Tenant, setup.Run.SessionId, human)).Items);
        Assert.Equal(TenantApprovalStatus.Rejected, rejected.Status); Assert.False(rejected.Waiting); Assert.Null(rejected.Outcome);
        await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, role.RoleId, role.Name, ["sessions.read"], await Revision());
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ListAsync(setup.Tenant, setup.Run.SessionId, human).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ListAsync(Guid.NewGuid(), setup.Run.SessionId, setup.Owner).AsTask());
    }
}
