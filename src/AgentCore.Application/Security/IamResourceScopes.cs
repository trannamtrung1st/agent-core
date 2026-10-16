using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

// These resources have authoritative ownership in the tenant security aggregate. Other families
// must acquire an owning-store resolver during their cutover; an arbitrary string is never an owner.
public static class IamResourceScopes
{
    public static AccessScope Create(Guid tenantId, string? resourceType, string? resourceId)
    {
        if (resourceType is null && resourceId is null) return AccessScope.ForTenant(tenantId);
        if (tenantId == Guid.Empty || resourceType is not ("member" or "role" or "agent")
            || !Guid.TryParse(resourceId, out var id) || id == Guid.Empty)
            throw new AccessDeniedException("resource_unavailable");
        return AccessScope.ForResource(tenantId, resourceType, id.ToString("D"));
    }

    public static bool Exists(TenantSecurityState state, AccessScope scope)
    {
        if (scope.Kind != AccessScopeKind.Resource || scope.TenantId != state.Tenant.TenantId
            || !Guid.TryParse(scope.ResourceId, out var id) || id == Guid.Empty
            || scope.ResourceId != id.ToString("D")) return false;
        return scope.ResourceType switch
        {
            "member" => state.Users.Any(u => u.UserId == id),
            "role" => state.Roles.Any(r => r.RoleId == id),
            "agent" => state.SystemIdentities.Any(i => i.Kind == SystemIdentityKind.Agent && i.AgentInstanceId == id),
            _ => false
        };
    }
}
