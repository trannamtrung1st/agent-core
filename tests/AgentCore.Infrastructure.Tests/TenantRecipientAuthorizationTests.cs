using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed class TenantRecipientAuthorizationTests
{
    [Theory]
    [InlineData(false, "change")] [InlineData(true, "change")]
    [InlineData(false, "revoke")] [InlineData(true, "revoke")]
    [InlineData(false, "logout")] [InlineData(true, "logout")]
    [InlineData(false, "churn")] [InlineData(true, "churn")]
    public async Task Recipient_authorization_reloads_a_changed_security_snapshot_without_borrowing_retired_authority(bool sqlite, string boundary)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var tenant = owner.TenantId;
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        var ownerLogin = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        var agent = await f.Agents.CreateAsync(owner.OwnerPrincipalId, tenant, "examiner", 1, await Revision());
        var session = await f.Sessions.CreateAsync(tenant, agent.Instance.InstanceId,
            new(owner.AccountId, ownerLogin.SessionId, owner.OwnerPrincipalId), SessionMode.Text, await Revision(), 1);
        var account = await f.Identity.RegisterAsync("recipient@example.test", "Recipient", "strong-recipient-password");
        var role = await f.Access.SaveRoleAsync(owner.OwnerPrincipalId, tenant, null, "Recipient", ["sessions.read", "agents.chat"], await Revision());
        var invitation = await f.Access.InviteAsync(owner.OwnerPrincipalId, tenant, account.LoginKey, role.RoleId, await Revision());
        var member = await f.Access.AcceptInvitationAsync(account.AccountId, tenant, invitation.Invitation.InvitationId, invitation.Token);
        var login = (await f.Identity.SignInAsync(account.LoginKey, "strong-recipient-password"))!.Session;
        var human = new TenantSessionInitiator(account.AccountId, login.SessionId, member.PrincipalId);
        var assignment = (await f.Iam.ReadTenantAsync(tenant))!.Assignments.Single(a => a.PrincipalId == member.PrincipalId && a.RoleId == role.RoleId);
        var gate = new ChangingSessionStore((ITenantSessionStore)f.Iam);
        var changes = 0;
        gate.AfterRead = async () =>
        {
            changes++;
            if (boundary != "churn") gate.AfterRead = null;
            if (boundary == "revoke") await f.Access.RevokeAssignmentAsync(owner.OwnerPrincipalId, tenant, assignment.AssignmentId, await Revision());
            else if (boundary == "logout") await f.Identity.SignOutAsync(login.SessionId);
            else await f.Access.SaveRoleAsync(owner.OwnerPrincipalId, tenant, null, $"Concurrent change {changes}", ["agents.read"], await Revision());
        };
        var service = new TenantConversationService(f.Iam, gate, (ITenantConversationStore)f.Iam, f.Identity, f.Authorizer);
        if (boundary is "revoke" or "logout")
        {
            await Assert.ThrowsAsync<AccessDeniedException>(() => service.AuthorizeRecipientAsync(tenant, session.Owner.SessionId, human).AsTask());
            Assert.Equal(1, changes);
        }
        else if (boundary == "churn")
        {
            var failure = await Assert.ThrowsAsync<SecurityConflictException>(() => service.AuthorizeRecipientAsync(tenant, session.Owner.SessionId, human).AsTask());
            Assert.Equal("stale_revision", failure.ReasonCode); Assert.Equal(3, changes); Assert.Equal(3, gate.Reads);
            gate.AfterRead = null;
            Assert.Equal(session.Owner, (await service.AuthorizeRecipientAsync(tenant, session.Owner.SessionId, human)).Owner);
        }
        else
        {
            var recipient = await service.AuthorizeRecipientAsync(tenant, session.Owner.SessionId, human);
            Assert.Equal(session.Owner, recipient.Owner); Assert.Equal(await Revision(), recipient.SecurityRevision);
            Assert.Equal(1, changes); Assert.Equal(2, gate.Reads);
        }
        Assert.Empty((await ((ITenantRunStore)f.Iam).ListTenantRunIdsAsync(tenant, session.Owner.SessionId)));
    }

    private sealed class ChangingSessionStore(ITenantSessionStore inner) : ITenantSessionStore
    {
        public Func<ValueTask>? AfterRead { get; set; }
        public int Reads { get; private set; }
        public async ValueTask<TenantSession?> FindTenantSessionAsync(Guid tenantId, Guid sessionId, CancellationToken ct = default)
        {
            Reads++; var value = await inner.FindTenantSessionAsync(tenantId, sessionId, ct);
            if (AfterRead is { } action) await action();
            return value;
        }
        public ValueTask<TenantSession?> TryChangeTenantSessionAsync(TenantSecurityState next, long expectedSecurityRevision, TenantSessionChange change, TenantSessionInitiator initiator, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default) => inner.TryChangeTenantSessionAsync(next, expectedSecurityRevision, change, initiator, at, audit, ct);
        public ValueTask<TenantSessionPage> ListTenantSessionsAsync(Guid tenantId, IReadOnlyList<Guid>? allowedIds, int page, int pageSize, CancellationToken ct = default, bool includeArchived = true, SessionSurface? surface = null, Guid? instanceId = null, bool recentFirst = false) => inner.ListTenantSessionsAsync(tenantId, allowedIds, page, pageSize, ct, includeArchived, surface, instanceId, recentFirst);
        public ValueTask<bool> TryCreateTenantSessionAsync(TenantSecurityState next, long expectedSecurityRevision, TenantSessionRoot owner, SessionSnapshot snapshot, TenantSessionInitiator initiator, long expectedInstanceRevision, AuthorizationAudit audit, CancellationToken ct = default) => inner.TryCreateTenantSessionAsync(next, expectedSecurityRevision, owner, snapshot, initiator, expectedInstanceRevision, audit, ct);
    }
}
