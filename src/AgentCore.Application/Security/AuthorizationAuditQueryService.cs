using AgentCore.Application.Ports;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed class AuthorizationAuditQueryService(IIamStore security, IAuthorizationAuditQueryStore audit,
    IdentityService identity, IamAuthorizer authorizer)
{
    private async ValueTask<TenantSecurityState> RequireTenant(Guid tenant, HumanSessionReference human, CancellationToken ct)
    {
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        if (!(await authorizer.CheckOwnedTenantAsync(state, human.PrincipalId, PermissionCatalog.Get("security.audit.read"), AccessScope.ForTenant(tenant), ct).ConfigureAwait(false)).Allowed)
            throw new AccessDeniedException("permission_denied");
        return state;
    }
    public async ValueTask<AuthorizationAuditPage> ReadTenantAsync(Guid tenant, HumanSessionReference human, AuthorizationAuditQuery query, CancellationToken ct = default)
    {
        var before = await RequireTenant(tenant, human, ct).ConfigureAwait(false);
        var result = await audit.QueryAuditAsync(tenant, AuthorizationAuditQueryRules.Normalize(query), ct).ConfigureAwait(false);
        var after = await RequireTenant(tenant, human, ct).ConfigureAwait(false);
        if (before.Tenant.Revision != after.Tenant.Revision) throw new SecurityConflictException("stale_revision");
        return result;
    }
    public async ValueTask<AuthorizationAuditPage> ReadSystemAsync(HumanSessionReference human, AuthorizationAuditQuery query, CancellationToken ct = default)
    {
        await SystemHumanSessionAuthority.RequireAsync(security, identity, authorizer, human, "system.governance.manage", ct).ConfigureAwait(false);
        var result = await audit.QueryAuditAsync(null, AuthorizationAuditQueryRules.Normalize(query), ct).ConfigureAwait(false);
        await SystemHumanSessionAuthority.RequireAsync(security, identity, authorizer, human, "system.governance.manage", ct).ConfigureAwait(false);
        return result with { Items = result.Items.Select(AuthorizationAuditQueryRules.SystemSafe).ToArray() };
    }
}
