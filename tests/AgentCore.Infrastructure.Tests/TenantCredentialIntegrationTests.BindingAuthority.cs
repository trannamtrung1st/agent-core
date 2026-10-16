using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Binding_metadata_and_mutations_require_independent_parent_and_exact_credential_grants(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var a = await f.Service.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, Password("Visible", "visible-secret"), await Revision());
        var b = await f.Service.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, Password("Hidden", "hidden-secret"), await Revision());
        var agent = await f.Agents.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "examiner", 1, await Revision()); var id = agent.Instance.InstanceId;
        var first = await f.BindingService.BindAsync(owner.OwnerPrincipalId, owner.TenantId, id, a.Credential.CredentialId, "visible", await Revision(), 1, 1);
        await f.BindingService.BindAsync(owner.OwnerPrincipalId, owner.TenantId, id, b.Credential.CredentialId, "hidden", await Revision(), 2, 1);
        var user = await AddUser(f, owner.TenantId, owner.OwnerPrincipalId, "agent-operator");
        var principal = user.User.PrincipalId;
        var parent = await Grant(f, owner.TenantId, owner.OwnerPrincipalId, principal, ["agents.read", "agents.update"], AccessScope.ForResource(owner.TenantId, "agent", id.ToString("D")));
        var scope = TenantCredentialRules.Scope(owner.TenantId, a.Credential.CredentialId);
        var read = await Grant(f, owner.TenantId, owner.OwnerPrincipalId, principal, ["credentials.read"], scope);
        var page = await f.BindingService.ListAsync(principal, owner.TenantId, id, 0, 1);
        Assert.False(page.AllRead); Assert.Equal(1, page.Total); Assert.Equal("visible", Assert.Single(page.Items).Reference);
        Assert.Empty((await f.BindingService.ListAsync(principal, owner.TenantId, id, 1, 1)).Items);
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.BindingService.UnbindAsync(principal, owner.TenantId, id, first.Binding.BindingId, 1, 1, 1).AsTask());
        var bind = await Grant(f, owner.TenantId, owner.OwnerPrincipalId, principal, ["credentials.bind"], scope);
        Assert.False((await f.Authorizer.CheckAsync(principal, "credentials.use", scope)).Allowed);
        await f.Access.RevokeAssignmentAsync(owner.OwnerPrincipalId, owner.TenantId, read.AssignmentId, await Revision());
        Assert.Equal(0, (await f.BindingService.ListAsync(principal, owner.TenantId, id)).Total);
        var removed = await f.BindingService.UnbindAsync(principal, owner.TenantId, id, first.Binding.BindingId, await Revision(), 3, 1);
        Assert.Equal(4, removed.InstanceRevision); Assert.DoesNotContain("credential", JsonSerializer.Serialize(removed), StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.BindingService.BindAsync(principal, owner.TenantId, id, a.Credential.CredentialId, "visible", 1, 1, 1).AsTask());
        await Grant(f, owner.TenantId, owner.OwnerPrincipalId, principal, ["credentials.read"], scope);
        var rebound = await f.BindingService.BindAsync(principal, owner.TenantId, id, a.Credential.CredentialId, "visible", await Revision(), 4, 1);
        Assert.NotEqual(first.Binding.BindingId, rebound.Binding.BindingId);
        await f.Access.RevokeAssignmentAsync(owner.OwnerPrincipalId, owner.TenantId, bind.AssignmentId, await Revision());
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.BindingService.UnbindAsync(principal, owner.TenantId, id, rebound.Binding.BindingId, 1, 1, 1).AsTask());
        Assert.Equal(5, (await f.Agents.ReadAsync(owner.OwnerPrincipalId, owner.TenantId, id)).Instance.Revision);
        await Grant(f, owner.TenantId, owner.OwnerPrincipalId, principal, ["credentials.bind"], scope);
        await f.Access.RevokeAssignmentAsync(owner.OwnerPrincipalId, owner.TenantId, parent.AssignmentId, await Revision());
        var fresh = await Revision();
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.BindingService.BindAsync(principal, owner.TenantId, id, a.Credential.CredentialId, "denied", fresh, 5, 1).AsTask());
        Assert.Equal(2, (await f.BindingService.ListAsync(owner.OwnerPrincipalId, owner.TenantId, id)).Total);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Prepared_binding_writes_fence_suspended_accounts_and_commit_only_one_racer(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var writer = await AddUser(f, owner.TenantId, owner.OwnerPrincipalId, "contributor");
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var value = await f.Service.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, Password("Shared", "private-value"), await Revision());
        var parent = await f.Agents.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "examiner", 1, await Revision());
        var now = DateTimeOffset.UtcNow; var binding = new AgentCredentialBinding(f.Ids.NewId(), parent.Instance.InstanceId, value.Credential.CredentialId, "prepared", 1, now, now);
        var store = (ITenantCredentialBindingStore)f.Iam; var state = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        var next = state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } };
        var system = (await f.Iam.ReadSystemAsync()).Principals.Single(p => p.AccountId == owner.AccountId);
        await new AccountAdministrationService(f.Iam, f.Authorizer, f.Tokens, f.Ids, TimeProvider.System).SetStatusAsync(system.PrincipalId, writer.Account.AccountId, IdentityStatus.Suspended);
        var auditBefore = (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count;
        var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => store.TryBindTenantCredentialAsync(next, state.Tenant.Revision, binding, 1, 1,
            f.Audit(owner.TenantId, writer.User.PrincipalId, parent.Instance.InstanceId)).AsTask());
        Assert.Equal("account_unavailable", denied.ReasonCode); Assert.Equal(state.Tenant.Revision, await Revision());
        Assert.Empty((await store.ReadTenantCredentialBindingsAsync(owner.TenantId, parent.Instance.InstanceId))!.Bindings);
        var outcomes = await Task.WhenAll(store.TryBindTenantCredentialAsync(next, state.Tenant.Revision, binding, 1, 1, f.Audit(owner.TenantId, owner.OwnerPrincipalId, parent.Instance.InstanceId)).AsTask(),
            store.TryBindTenantCredentialAsync(next, state.Tenant.Revision, binding, 1, 1, f.Audit(owner.TenantId, owner.OwnerPrincipalId, parent.Instance.InstanceId)).AsTask());
        Assert.Single(outcomes, result => result is not null); Assert.Equal(auditBefore + 1, (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count);
        Assert.Equal(2, (await store.ReadTenantCredentialBindingsAsync(owner.TenantId, parent.Instance.InstanceId))!.Agent.Instance.Revision);
        state = (await f.Iam.ReadTenantAsync(owner.TenantId))!; next = state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } };
        await Assert.ThrowsAsync<AccessDeniedException>(() => store.TryUnbindTenantCredentialAsync(next, state.Tenant.Revision, parent.Instance.InstanceId, binding.BindingId, 2, 1,
            now, f.Audit(owner.TenantId, writer.User.PrincipalId, parent.Instance.InstanceId)).AsTask());
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.TryUnbindTenantCredentialAsync(next, state.Tenant.Revision, parent.Instance.InstanceId, binding.BindingId, 2, 1,
            now, f.Audit(owner.TenantId, owner.OwnerPrincipalId, parent.Instance.InstanceId), cancelled.Token).AsTask());
        Assert.Equal(state.Tenant.Revision, await Revision()); Assert.Single((await store.ReadTenantCredentialBindingsAsync(owner.TenantId, parent.Instance.InstanceId))!.Bindings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Agent_binding_changes_require_current_connections_policy_and_freeze_never_grants_use(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var credential = await f.Service.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, Password("Private", "private-value"), await Revision());
        var agent = await f.Agents.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "examiner", 1, await Revision()); var id = agent.Instance.InstanceId;
        await Grant(f, owner.TenantId, owner.OwnerPrincipalId, agent.PrincipalId, ["agents.read", "agents.update"], AccessScope.ForResource(owner.TenantId, "agent", id.ToString("D")));
        var credentialScope = TenantCredentialRules.Scope(owner.TenantId, credential.Credential.CredentialId);
        await Grant(f, owner.TenantId, owner.OwnerPrincipalId, agent.PrincipalId, ["credentials.read", "credentials.bind"], credentialScope);
        var before = await Revision();
        var disabled = await Assert.ThrowsAsync<AccessDeniedException>(() => f.BindingService.BindAsync(agent.PrincipalId, owner.TenantId, id, credential.Credential.CredentialId, "primary", before, 1, 1).AsTask());
        Assert.Equal("management_scope_denied", disabled.ReasonCode);
        var policy = new HarnessManagementState(new(HarnessManagementMode.Assisted, [HarnessManagementScope.Connections], [], []));
        var state = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        var changed = await ((ITenantAgentStore)f.Iam).TryChangeTenantAgentConfigurationAsync(state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } }, state.Tenant.Revision,
            new(id, 1, HarnessManagement: policy), DateTimeOffset.UtcNow, f.Audit(owner.TenantId, owner.OwnerPrincipalId, id));
        var bound = await f.BindingService.BindAsync(agent.PrincipalId, owner.TenantId, id, credential.Credential.CredentialId, "primary", await Revision(), changed!.Instance.Revision, 1);
        Assert.False((await f.Authorizer.CheckAsync(agent.PrincipalId, "credentials.use", credentialScope)).Allowed);
        state = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        var frozen = await ((ITenantAgentStore)f.Iam).TryChangeTenantAgentConfigurationAsync(state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } }, state.Tenant.Revision,
            new(id, bound.InstanceRevision, HarnessManagement: policy with { Policy = policy.Policy with { Frozen = true }, PolicyRevision = 2 }), DateTimeOffset.UtcNow, f.Audit(owner.TenantId, owner.OwnerPrincipalId, id));
        before = await Revision();
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.BindingService.UnbindAsync(agent.PrincipalId, owner.TenantId, id, bound.Binding.BindingId, before, frozen!.Instance.Revision, 1).AsTask());
        await f.BindingService.UnbindAsync(owner.OwnerPrincipalId, owner.TenantId, id, bound.Binding.BindingId, await Revision(), frozen!.Instance.Revision, 1);
    }

    [Fact]
    public async Task Sqlite_binding_composite_foreign_keys_reject_both_cross_tenant_edges_and_leave_no_rebuild_indexes()
    {
        await using var f = await Fixture.Create(true);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var ca = await f.Service.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, Password("Alpha", "private-value"), 1);
        var a = await f.Agents.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "examiner", 1, 2);
        var beta = await f.Access.CreateTenantAsync((await f.Iam.ReadSystemAsync()).Principals.Single().PrincipalId, owner.AccountId, "Beta", "beta");
        var betaActor = beta.Users.Single().PrincipalId;
        var cb = await f.Service.CreateAsync(betaActor, beta.Tenant.TenantId, Password("Beta", "private-value"), 1);
        var b = await f.Agents.CreateAsync(betaActor, beta.Tenant.TenantId, "examiner", 1, 2);
        foreach (var wrongCredential in new[] { true, false })
        {
            await using var db = f.Contexts!.CreateDbContext();
            db.AgentCredentialBindings.Add(new() { TenantId = owner.TenantId, BindingId = f.Ids.NewId().ToString("D"),
                AgentInstanceId = (wrongCredential ? a : b).Instance.InstanceId.ToString("D"), CredentialId = (wrongCredential ? cb : ca).Credential.CredentialId.ToString("D"),
                Reference = "foreign", Revision = 1 });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        await using var check = f.Contexts!.CreateDbContext();
        Assert.Empty(await check.AgentCredentialBindings.ToArrayAsync());
        Assert.Empty(await check.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type='index' AND name LIKE 'IX_Binding%RebuildWitness'").ToArrayAsync());
        Assert.False(check.Database.HasPendingModelChanges());
        var state = (await f.Iam.ReadTenantAsync(owner.TenantId))!; var now = DateTimeOffset.UtcNow;
        var foreign = new AgentCredentialBinding(f.Ids.NewId(), a.Instance.InstanceId, cb.Credential.CredentialId, "foreign", 1, now, now);
        Assert.Null(await ((ITenantCredentialBindingStore)f.Iam).TryBindTenantCredentialAsync(state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } }, state.Tenant.Revision,
            foreign, 1, 1, f.Audit(owner.TenantId, owner.OwnerPrincipalId, a.Instance.InstanceId)));
    }
    private static async Task<RoleAssignment> Grant(Fixture f, Guid tenant, Guid actor, Guid principal, string[] permissions, AccessScope scope)
    {
        var state = (await f.Iam.ReadTenantAsync(tenant))!;
        var role = await f.Access.SaveRoleAsync(actor, tenant, null, "Focused grant", permissions, state.Tenant.Revision);
        state = (await f.Iam.ReadTenantAsync(tenant))!;
        return await f.Access.AssignRoleAsync(actor, tenant, principal, role.RoleId, scope, state.Tenant.Revision);
    }
}
