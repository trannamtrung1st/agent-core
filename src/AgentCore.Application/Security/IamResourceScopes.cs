using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

// Member/role/agent ownership is in security state; Definitions, Credentials and qualified
// children resolve through their actual stores. An arbitrary string is never an owner.
public static class IamResourceScopes
{
    public static AccessScope Create(Guid tenantId, string? resourceType, string? resourceId)
    {
        if (resourceType is null && resourceId is null) return AccessScope.ForTenant(tenantId);
        if (resourceType is "skill" or "resource")
        {
            var child = AccessScope.ForResource(tenantId, resourceType, resourceId ?? "");
            if (!TenantAgentChildScopes.TryParse(child, out _, out _) && !TenantDefinitionResourceScopes.TryParse(child, out _)) throw new AccessDeniedException("resource_unavailable");
            return child;
        }
        if (tenantId == Guid.Empty || resourceType is not ("member" or "role" or "agent" or "definition" or "credential" or "session" or "agent-run")
            || !Guid.TryParse(resourceId, out var id) || id == Guid.Empty)
            throw new AccessDeniedException("resource_unavailable");
        return AccessScope.ForResource(tenantId, resourceType, id.ToString("D"));
    }

    public static bool ExternalShape(AccessScope scope) => scope.ResourceType == "definition"
        ? Guid.TryParseExact(scope.ResourceId, "D", out var id) && id != Guid.Empty && scope.ResourceId == id.ToString("D")
        : TenantRunRules.TryId(scope, out _) || TenantSessionRules.TryId(scope, out _) || TenantCredentialRules.TryId(scope, out _) || TenantAgentChildScopes.TryParse(scope, out _, out _) || TenantDefinitionResourceScopes.TryParse(scope, out _);

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
