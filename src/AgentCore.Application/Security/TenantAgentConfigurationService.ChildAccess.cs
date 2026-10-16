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
        var visible = await authorizer.VisibleResourcesAsync(state, actor, permission, items, item => ChildScope(tenant, id, permission, key(item)), ct).ConfigureAwait(false);
        if (requireAny && !visible.All && visible.Items.Length == 0) throw new AccessDeniedException("permission_denied");
        return visible;
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
