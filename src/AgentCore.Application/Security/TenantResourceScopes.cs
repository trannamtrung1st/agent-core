using AgentCore.Domain.Security;
using AgentCore.Domain.Spaces;

namespace AgentCore.Application.Security;

// Execution Instance and immutable source owner are distinct. Only the closed origin selects a scope.
public static class TenantResourceScopes
{
    public static AccessScope Source(Guid tenant, Guid instance, string key) => SpaceResourceKeys.TryParse(key, out _, out _)
        ? AccessScope.ForResource(tenant, "resource", key) : TenantAgentChildScopes.Scope(tenant, instance, "resource", key);
    public static bool TrySpace(AccessScope scope, out Guid space, out Guid resource)
    {
        space = resource = Guid.Empty;
        return scope is { Kind: AccessScopeKind.Resource, ResourceType: "resource", TenantId: not null }
            && SpaceResourceKeys.TryParse(scope.ResourceId, out space, out resource);
    }
    public static bool IsKey(string key) => TenantAgentChildScopes.IsResourceKey(key) || SpaceResourceKeys.TryParse(key, out _, out _);
}
