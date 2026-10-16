using AgentCore.Application.Admin;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantAgentConfigurationService
{
    private async ValueTask<TenantSecurityState> ChildParentState(Guid actor, Guid tenant, Guid id, CancellationToken ct)
    {
        var state = await security.ReadTenantAsync(tenant, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        await RequireChildPermission(state, actor, "agents.read", Scope(tenant, id), ct).ConfigureAwait(false);
        return state;
    }
    private async ValueTask RequireChildPermission(TenantSecurityState state, Guid actor, string permission, AccessScope scope, CancellationToken ct)
    {
        var decision = await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get(permission), scope, ct).ConfigureAwait(false);
        if (!decision.Allowed) throw new AccessDeniedException(decision.ReasonCode);
    }
    private static AccessScope ChildScope(Guid tenant, Guid id, string permission, string? key) => key is null
        ? AccessScope.ForTenant(tenant) : TenantAgentChildScopes.Scope(tenant, id, permission.StartsWith("skills.", StringComparison.Ordinal) ? "skill" : "resource", key);
    private async ValueTask<TenantSecurityState> ChildReadState(Guid actor, Guid tenant, Guid id, string permission, CancellationToken ct, string? key = null)
    {
        var state = await ChildParentState(actor, tenant, id, ct).ConfigureAwait(false);
        await RequireChildPermission(state, actor, permission, ChildScope(tenant, id, permission, key), ct).ConfigureAwait(false);
        return state;
    }
    private async ValueTask<TenantSecurityState> ChildChangeState(Guid actor, Guid tenant, Guid id, string permission, string read, long expected, CancellationToken ct, string? key = null)
    {
        var state = await ChildReadState(actor, tenant, id, read, ct, key).ConfigureAwait(false);
        await RequireChildPermission(state, actor, permission, ChildScope(tenant, id, permission, key), ct).ConfigureAwait(false);
        if (state.Tenant.Revision != expected) throw new SecurityConflictException("stale_revision");
        return state;
    }
    private async ValueTask<(T[] Items, bool All)> VisibleChildren<T>(TenantSecurityState state, Guid actor, Guid tenant, Guid id,
        string permission, IReadOnlyList<T> items, Func<T, string> key, CancellationToken ct, bool requireAny = false)
    {
        var metadata = PermissionCatalog.Get(permission);
        var inherited = await authorizer.CheckTenantAsync(state, actor, metadata, AccessScope.ForTenant(tenant), ct).ConfigureAwait(false);
        if (inherited.Allowed) return (items.ToArray(), true);
        if (inherited.ReasonCode != "permission_denied") throw new AccessDeniedException(inherited.ReasonCode);
        var visible = new List<T>();
        foreach (var item in items)
        {
            var scope = ChildScope(tenant, id, permission, key(item));
            // Skip ungranted keys before an owning-store lookup; counts/pages are based only on visible rows.
            if (!state.Assignments.Any(a => a.PrincipalId == actor && a.Scope == scope
                && state.Roles.Any(r => r.RoleId == a.RoleId && r.Permissions.Contains(permission, StringComparer.Ordinal)))) continue;
            var decision = await authorizer.CheckTenantAsync(state, actor, metadata, scope, ct).ConfigureAwait(false);
            if (decision.Allowed) visible.Add(item);
            else if (decision.ReasonCode is not ("permission_denied" or "resource_unavailable")) throw new AccessDeniedException(decision.ReasonCode);
        }
        if (requireAny && visible.Count == 0) throw new AccessDeniedException("permission_denied");
        return (visible.ToArray(), false);
    }
    private async ValueTask<InstanceResourceView[]> SafeResourceViews(TenantSecurityState state, Guid actor, Guid tenant, Guid id,
        IReadOnlyList<InstanceResourceView> views, CancellationToken ct)
    {
        var dependencies = views.SelectMany(r => r.Dependencies).Distinct(StringComparer.Ordinal).ToArray();
        var visible = await VisibleChildren(state, actor, tenant, id, "skills.read", dependencies, key => key, ct).ConfigureAwait(false);
        var allowed = visible.Items.ToHashSet(StringComparer.Ordinal);
        return views.Select(r => r with { Dependencies = r.Dependencies.Where(allowed.Contains).ToArray() }).ToArray();
    }
}
