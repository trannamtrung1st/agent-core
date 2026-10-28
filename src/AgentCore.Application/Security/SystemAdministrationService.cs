using AgentCore.Application.Ports;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed class SystemAdministrationService(IIamStore security, ISystemAdministrationStore store, IdentityService identity,
    IamAuthorizer authorizer, IIdentityTokens tokens, IIdGenerator ids, TimeProvider time, ManagedGeneralAssistantService? assistant = null)
{
    private ValueTask Require(HumanSessionReference human, string permission, CancellationToken ct) => SystemHumanSessionAuthority.RequireAsync(security, identity, authorizer, human, permission, ct);
    public async ValueTask<SystemAccountPage> AccountsAsync(HumanSessionReference human, SystemCollectionQuery query, CancellationToken ct = default)
    {
        await Require(human, "system.accounts.manage", ct); var result = await store.QuerySystemAccountsAsync(SystemAdministrationRules.Normalize(query), ct);
        await Require(human, "system.accounts.manage", ct); return result;
    }
    public async ValueTask<SystemTenantPage> TenantsAsync(HumanSessionReference human, SystemCollectionQuery query, CancellationToken ct = default)
    {
        await Require(human, "system.tenants.read", ct); var result = await store.QuerySystemTenantsAsync(SystemAdministrationRules.Normalize(query), ct);
        await Require(human, "system.tenants.read", ct); return result;
    }
    public async ValueTask<Tenant> CreateTenantAsync(HumanSessionReference human, string name, string slug, CancellationToken ct = default)
    {
        await Require(human, "system.tenants.create", ct);
        var account = await security.ReadAccountAsync(human.AccountId, ct) ?? throw new AccessDeniedException("initiator_unavailable");
        var tenant = identity.CreateTenant(account, name, slug);
        if (!await store.TryCreateSystemTenantAsync(human, tenant, Audit(human, "system.tenants.create", tenant.Tenant.TenantId, tenant.Tenant.TenantId), ct)) throw new SecurityConflictException("tenant_exists");
        if (assistant is not null) { await assistant.ReconcileAsync(tenant.Tenant.TenantId, ct); return (await security.ReadTenantAsync(tenant.Tenant.TenantId, ct))!.Tenant; }
        return tenant.Tenant;
    }
    public async ValueTask SetAccountStatusAsync(HumanSessionReference human, Guid accountId, string expectedVersion, IdentityStatus status, CancellationToken ct = default)
    {
        await Require(human, "system.accounts.manage", ct); SystemAdministrationRules.AccountStatus(expectedVersion, status);
        if (!await store.TrySetSystemAccountStatusAsync(human, accountId, expectedVersion, status, tokens.Create(), Audit(human, "system.accounts.status", accountId), ct)) throw new SecurityConflictException("stale_account");
    }
    public async ValueTask SetTenantStatusAsync(HumanSessionReference human, Guid tenantId, long expectedRevision, IdentityStatus status, CancellationToken ct = default)
    {
        await Require(human, "system.tenants.manage", ct); SystemAdministrationRules.TenantStatus(expectedRevision, status);
        if (!await store.TrySetSystemTenantStatusAsync(human, tenantId, expectedRevision, status, Audit(human, "system.tenants.status", tenantId), ct)) throw new SecurityConflictException("stale_revision");
    }
    private AuthorizationAudit Audit(HumanSessionReference human, string action, Guid target, Guid? tenant = null) =>
        new(ids.NewId(), tenant, human.PrincipalId, action, target.ToString("D"), "allowed", "authorized", DateTimeOffset.FromUnixTimeMilliseconds(time.GetUtcNow().ToUnixTimeMilliseconds()));
}
