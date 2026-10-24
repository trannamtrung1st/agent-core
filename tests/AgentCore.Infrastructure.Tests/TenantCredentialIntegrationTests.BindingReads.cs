using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    [Theory]
    [InlineData(false, "credential")]
    [InlineData(true, "credential")]
    [InlineData(false, "parent")]
    [InlineData(true, "parent")]
    [InlineData(false, "account")]
    [InlineData(true, "account")]
    [InlineData(false, "unlink")]
    [InlineData(true, "unlink")]
    public async Task Binding_inventory_never_returns_a_projection_captured_before_authority_or_configuration_changes(bool sqlite, string change)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var credential = await f.Service.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, Password("Private account", "read-private-sentinel"), await Revision());
        var agent = await f.Agents.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "examiner", 1, await Revision());
        var id = agent.Instance.InstanceId;
        var binding = await f.BindingService.BindAsync(owner.OwnerPrincipalId, owner.TenantId, id, credential.Credential.CredentialId, "primary", await Revision(), 1, 1);
        var user = await AddUser(f, owner.TenantId, owner.OwnerPrincipalId, "approver");
        var parent = await Grant(f, owner.TenantId, owner.OwnerPrincipalId, user.User.PrincipalId, ["agents.read"], AccessScope.ForResource(owner.TenantId, "agent", id.ToString("D")));
        var read = await Grant(f, owner.TenantId, owner.OwnerPrincipalId, user.User.PrincipalId, ["credentials.read"], TenantCredentialRules.Scope(owner.TenantId, credential.Credential.CredentialId));
        var before = await f.BindingService.ListAsync(user.User.PrincipalId, owner.TenantId, id);
        Assert.Equal("Private account", Assert.Single(before.Items).Credential.DisplayName);
        AssertSafe(before, "read-private-sentinel");
        var intercepted = new AfterBindingSnapshot((ITenantCredentialBindingStore)f.Iam, async () =>
        {
            switch (change)
            {
                case "credential": await f.Access.RevokeAssignmentAsync(owner.OwnerPrincipalId, owner.TenantId, read.AssignmentId, await Revision()); break;
                case "parent": await f.Access.RevokeAssignmentAsync(owner.OwnerPrincipalId, owner.TenantId, parent.AssignmentId, await Revision()); break;
                case "account":
                    var system = (await f.Iam.ReadSystemAsync()).Principals.Single(p => p.AccountId == owner.AccountId);
                    await new AccountAdministrationService(f.Iam, f.Authorizer, f.Tokens, f.Ids, TimeProvider.System).SetStatusAsync(system.PrincipalId, user.Account.AccountId, IdentityStatus.Suspended);
                    break;
                case "unlink": await f.BindingService.UnbindAsync(owner.OwnerPrincipalId, owner.TenantId, id, binding.Binding.BindingId, await Revision(), 2, 1); break;
            }
        });
        var service = f.BindingServiceFor(intercepted);
        if (change is "parent" or "account")
            await Assert.ThrowsAsync<AccessDeniedException>(() => service.ListAsync(user.User.PrincipalId, owner.TenantId, id).AsTask());
        else
        {
            var conflict = await Assert.ThrowsAsync<SecurityConflictException>(() => service.ListAsync(user.User.PrincipalId, owner.TenantId, id).AsTask());
            Assert.Equal("stale_revision", conflict.ReasonCode);
            Assert.Empty((await f.BindingService.ListAsync(user.User.PrincipalId, owner.TenantId, id)).Items);
        }
        var current = await f.BindingService.ListAsync(owner.OwnerPrincipalId, owner.TenantId, id);
        Assert.Equal(change == "unlink" ? 0 : 1, current.Total);
        AssertSafe(current, "read-private-sentinel");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Human_binding_inventory_fences_logout_and_password_rotation_during_snapshot_IO(bool sqlite, bool rotatePassword)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var credential = await f.Service.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, Password("Private account", "read-private-sentinel"), await Revision());
        var agent = await f.Agents.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "examiner", 1, await Revision());
        var id = agent.Instance.InstanceId;
        await f.BindingService.BindAsync(owner.OwnerPrincipalId, owner.TenantId, id, credential.Credential.CredentialId, "primary", await Revision(), 1, 1);
        var session = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        var human = new TenantSessionInitiator(owner.AccountId, session.SessionId, owner.OwnerPrincipalId);
        Assert.Single((await f.BindingService.ListForHumanAsync(owner.TenantId, id, human)).Items);
        var beforeRevision = await Revision();
        var intercepted = new AfterBindingSnapshot((ITenantCredentialBindingStore)f.Iam, async () =>
        {
            if (rotatePassword) await f.Identity.ChangePasswordAsync(owner.AccountId, "strong-owner-password", "strong-replacement-password");
            else await f.Identity.SignOutAsync(session.SessionId);
        });
        var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => f.BindingServiceFor(intercepted).ListForHumanAsync(owner.TenantId, id, human).AsTask());
        Assert.Equal("initiator_unavailable", denied.ReasonCode);
        Assert.Equal(beforeRevision, await Revision()); // Account/session retirement is independent of tenant revision.
        Assert.Null(await f.Identity.ValidateSessionAsync(session.SessionId));
        var replacement = (await f.Identity.SignInAsync("owner@example.test", rotatePassword ? "strong-replacement-password" : "strong-owner-password"))!.Session;
        var recovered = await f.BindingService.ListForHumanAsync(owner.TenantId, id, human with { AuthenticationSessionId = replacement.SessionId });
        Assert.Equal("primary", Assert.Single(recovered.Items).Reference);
        AssertSafe(recovered, "read-private-sentinel");
    }

    // Captures a real store snapshot, then deterministically performs a concurrent change before
    // the Application service can project it. No sleeps or production-only interception hooks.
    private sealed class AfterBindingSnapshot(ITenantCredentialBindingStore inner, Func<Task> after) : ITenantCredentialBindingStore
    {
        private Func<Task>? pending = after;
        public async ValueTask<TenantCredentialBindingSnapshot?> ReadTenantCredentialBindingsAsync(Guid tenantId, Guid instanceId, CancellationToken ct = default)
        {
            var result = await inner.ReadTenantCredentialBindingsAsync(tenantId, instanceId, ct);
            if (Interlocked.Exchange(ref pending, null) is { } action) await action();
            return result;
        }
        public ValueTask<TenantAgent?> TryBindTenantCredentialAsync(TenantSecurityState next, long expectedSecurityRevision, AgentCredentialBinding binding,
            long expectedInstanceRevision, long expectedCredentialRevision, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null) =>
            inner.TryBindTenantCredentialAsync(next, expectedSecurityRevision, binding, expectedInstanceRevision, expectedCredentialRevision, audit, ct, initiator);
        public ValueTask<TenantAgent?> TryUnbindTenantCredentialAsync(TenantSecurityState next, long expectedSecurityRevision, Guid instanceId, Guid bindingId,
            long expectedInstanceRevision, long expectedBindingRevision, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null) =>
            inner.TryUnbindTenantCredentialAsync(next, expectedSecurityRevision, instanceId, bindingId, expectedInstanceRevision, expectedBindingRevision, at, audit, ct, initiator);
    }
}
