using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exact_credential_grants_filter_before_paging_revoke_immediately_and_retire_atomically(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var first = await f.Service.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, Password("First", "first-secret"), await Revision());
        var second = await f.Service.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, Password("Second", "second-secret"), await Revision());
        var user = await AddUser(f, owner.TenantId, owner.OwnerPrincipalId, "agent-operator");
        var id = first.Credential.CredentialId; var scope = TenantCredentialRules.Scope(owner.TenantId, id);
        var role = await f.Access.SaveRoleAsync(owner.OwnerPrincipalId, owner.TenantId, null, "One credential", ["credentials.read", "credentials.manage"], await Revision());
        var assignment = await f.Access.AssignRoleAsync(owner.OwnerPrincipalId, owner.TenantId, user.User.PrincipalId, role.RoleId, scope, await Revision());
        var page = await f.Service.ListAsync(user.User.PrincipalId, owner.TenantId, 0, 1);
        Assert.Equal(1, page.Total); Assert.Equal(id, Assert.Single(page.Items).Credential.CredentialId);
        Assert.Empty((await f.Service.ListAsync(user.User.PrincipalId, owner.TenantId, 1, 1)).Items);
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Service.ReadAsync(user.User.PrincipalId, owner.TenantId, second.Credential.CredentialId).AsTask());
        var securityRevision = await Revision();
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Service.CreateAsync(user.User.PrincipalId, owner.TenantId, Password("Denied", "no-creation"), securityRevision).AsTask());
        await f.Service.ReplaceAsync(user.User.PrincipalId, owner.TenantId, id, "exact-replacement", await Revision(), 1);
        await f.Access.RevokeAssignmentAsync(owner.OwnerPrincipalId, owner.TenantId, assignment.AssignmentId, await Revision());
        Assert.Equal(0, (await f.Service.ListAsync(user.User.PrincipalId, owner.TenantId)).Total);
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Service.ReplaceAsync(user.User.PrincipalId, owner.TenantId, id, "revoked", 1, 1).AsTask());
        var useRole = await f.Access.SaveRoleAsync(owner.OwnerPrincipalId, owner.TenantId, null, "Use only", ["credentials.use"], await Revision());
        await f.Access.AssignRoleAsync(owner.OwnerPrincipalId, owner.TenantId, user.User.PrincipalId, useRole.RoleId, scope, await Revision());
        Assert.True((await f.Authorizer.CheckAsync(user.User.PrincipalId, "credentials.use", scope)).Allowed);
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Service.ReadAsync(user.User.PrincipalId, owner.TenantId, id).AsTask());
        Assert.Null(await f.Legacy.GetAsync(id));
        var state = (await f.Iam.ReadTenantAsync(owner.TenantId))!; var current = (await f.Credentials.FindTenantCredentialAsync(owner.TenantId, id))!;
        var next = state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } };
        await Assert.ThrowsAsync<ArgumentException>(() => f.Credentials.TryDeleteTenantCredentialAsync(next, state.Tenant.Revision, id,
            current.Value.Revision, current.Value.SecretRevision, DateTimeOffset.UtcNow, f.Audit(owner.TenantId, owner.OwnerPrincipalId, id)).AsTask());
        Assert.Equal(state.Tenant.Revision, await Revision());
        Assert.Equal("exact-replacement", f.Protector.Unprotect(id, 1, (await f.Credentials.FindTenantCredentialAsync(owner.TenantId, id))!.Value.ProtectedPayload));
        await f.Service.DeleteAsync(owner.OwnerPrincipalId, owner.TenantId, id, await Revision(), 2);
        state = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        Assert.DoesNotContain(state.Assignments, a => a.Scope == scope);
        Assert.False((await f.Authorizer.CheckAsync(user.User.PrincipalId, "credentials.use", scope)).Allowed);
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Access.AssignRoleAsync(owner.OwnerPrincipalId, owner.TenantId, user.User.PrincipalId, role.RoleId, scope, state.Tenant.Revision).AsTask());
        Assert.NotNull(await f.Credentials.FindTenantCredentialAsync(owner.TenantId, second.Credential.CredentialId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Credential_transactions_fence_account_suspension_and_stale_secret_generations(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var writer = await AddUser(f, owner.TenantId, owner.OwnerPrincipalId, "contributor");
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var created = await f.Service.CreateAsync(writer.User.PrincipalId, owner.TenantId, Password("Prepared", "initial-value"), await Revision());
        var id = created.Credential.CredentialId; var state = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        var current = (await f.Credentials.FindTenantCredentialAsync(owner.TenantId, id))!;
        var next = state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } };
        var prepared = current.Value with { DisplayName = "Suspended writer", Revision = 2 };
        var system = (await f.Iam.ReadSystemAsync()).Principals.Single(p => p.AccountId == owner.AccountId);
        await new AccountAdministrationService(f.Iam, f.Authorizer, f.Tokens, f.Ids, TimeProvider.System)
            .SetStatusAsync(system.PrincipalId, writer.Account.AccountId, IdentityStatus.Suspended);
        var before = (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count;
        var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => f.Credentials.TrySaveTenantCredentialAsync(next, state.Tenant.Revision,
            current.Owner, prepared, 1, 0, f.Audit(owner.TenantId, writer.User.PrincipalId, id)).AsTask());
        Assert.Equal("account_unavailable", denied.ReasonCode); Assert.Equal(state.Tenant.Revision, await Revision());
        Assert.Equal(before, (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count);
        var outcomes = await Task.WhenAll(
            f.Credentials.TrySaveTenantCredentialAsync(next, state.Tenant.Revision, current.Owner, prepared with { DisplayName = "Racer A" }, 1, 0, f.Audit(owner.TenantId, owner.OwnerPrincipalId, id)).AsTask(),
            f.Credentials.TrySaveTenantCredentialAsync(next, state.Tenant.Revision, current.Owner, prepared with { DisplayName = "Racer B" }, 1, 0, f.Audit(owner.TenantId, owner.OwnerPrincipalId, id)).AsTask());
        Assert.Single(outcomes, won => won);
        Assert.Equal(before + 1, (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count);
        await f.Service.ReplaceAsync(owner.OwnerPrincipalId, owner.TenantId, id, "new-generation", await Revision(), 2);
        state = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        current = (await f.Credentials.FindTenantCredentialAsync(owner.TenantId, id))!;
        next = state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } };
        Assert.False(await f.Credentials.TrySaveTenantCredentialAsync(next, state.Tenant.Revision, current.Owner,
            current.Value with { Revision = current.Value.Revision + 1 }, current.Value.Revision, 0, f.Audit(owner.TenantId, owner.OwnerPrincipalId, id)));
        Assert.Equal(state.Tenant.Revision, await Revision());
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Credentials.TryDeleteTenantCredentialAsync(next, state.Tenant.Revision,
            id, current.Value.Revision, current.Value.SecretRevision, DateTimeOffset.UtcNow, f.Audit(owner.TenantId, owner.OwnerPrincipalId, id), cancel.Token).AsTask());
        Assert.Equal("new-generation", f.Protector.Unprotect(id, 1, (await f.Credentials.FindTenantCredentialAsync(owner.TenantId, id))!.Value.ProtectedPayload));
    }
    private static async Task<(Account Account, TenantUser User)> AddUser(Fixture f, Guid tenant, Guid actor, string roleKey)
    {
        var account = await f.Identity.RegisterAsync("writer@example.test", "Writer", "strong-writer-password");
        var state = (await f.Iam.ReadTenantAsync(tenant))!;
        var invitation = await f.Access.InviteAsync(actor, tenant, account.LoginKey, state.Roles.Single(r => r.Key == roleKey).RoleId, state.Tenant.Revision);
        var user = await f.Access.AcceptInvitationAsync(account.AccountId, tenant, invitation.Invitation.InvitationId, invitation.Token);
        return (account, user);
    }
}
