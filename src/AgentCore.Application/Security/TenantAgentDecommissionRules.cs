using AgentCore.Application.Ports;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantAgentDecommissionRules
{
    public const string Action = "agents.decommission";
    public static AccessScope Scope(Guid tenant, Guid instance) => AccessScope.ForResource(tenant, "agent", instance.ToString("D"));
    public static async ValueTask<bool> Allowed(TenantSecurityState state, Guid actor, Guid instance, IamAuthorizer authorizer, bool delete, CancellationToken ct)
    {
        foreach (var key in delete ? new[] { "agents.read", "agents.delete" } : new[] { "agents.read" })
            if (!(await authorizer.CheckOwnedTenantAsync(state, actor, PermissionCatalog.Get(key), Scope(state.Tenant.TenantId, instance), ct)).Allowed) return false;
        return true;
    }
    public static TenantSecurityState Retire(TenantSecurityState state, Guid instance, Guid principal, Guid privateSpace) => IamStateRules.Freeze(state with {
        Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) },
        SystemIdentities = state.SystemIdentities.Select(i => i.AgentInstanceId == instance ? i with { Status = IdentityStatus.Removed } : i).ToArray(),
        Principals = state.Principals.Select(p => p.PrincipalId == principal ? p with { Status = IdentityStatus.Removed } : p).ToArray(),
        Assignments = state.Assignments.Where(a => a.PrincipalId != principal && !BelongsTo(a.Scope, instance, privateSpace)).ToArray()
    });
    private static bool BelongsTo(AccessScope scope, Guid instance, Guid privateSpace) => scope.ResourceType == "agent" && scope.ResourceId == instance.ToString("D")
        || TenantAgentChildScopes.BelongsTo(scope, instance) || scope.ResourceType == "workspace" && scope.ResourceId == "home:" + privateSpace.ToString("D")
        || scope.ResourceType == "space" && scope.ResourceId == privateSpace.ToString("D")
        || TenantResourceScopes.TrySpace(scope, out var sourceSpace, out _) && sourceSpace == privateSpace;
    public static void Validate(Guid tenant, Guid instance, HumanSessionReference human, AuthorizationAudit audit)
    {
        if (tenant == Guid.Empty || instance == Guid.Empty || audit.AuditId == Guid.Empty || audit.TenantId != tenant
            || audit.ActorPrincipalId != human.PrincipalId || audit.Action != Action || audit.Target != instance.ToString("D")
            || audit.Decision != "allowed" || audit.ReasonCode != "agent_decommissioned" || audit.Execution is not null || audit.At.Offset != TimeSpan.Zero)
            throw new ArgumentException("Use exact Agent decommission facts.");
    }
}
