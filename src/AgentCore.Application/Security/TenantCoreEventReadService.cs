using AgentCore.Application.Ports;
using AgentCore.Domain.Events;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantCoreEventReadPage(TenantCoreEventPage Page, long SecurityRevision);
public sealed class TenantCoreEventReadService(IIamStore security, ITenantEventStore events, ITenantAgentStore agents,
    IdentityService identity, IamAuthorizer iam)
{
    private async ValueTask RequireAgent(TenantSecurityState state, Guid human, Guid instance, CancellationToken ct)
    {
        if (await agents.FindTenantAgentAsync(state.Tenant.TenantId, instance, ct).ConfigureAwait(false) is null
            || !(await iam.CheckOwnedTenantAsync(state, human, PermissionCatalog.Get("agents.read"),
                AccessScope.ForResource(state.Tenant.TenantId, "agent", instance.ToString("D")), ct).ConfigureAwait(false)).Allowed)
            throw new AccessDeniedException("event_source_unavailable");
    }
    public async ValueTask<TenantCoreEventReadPage> ListAsync(Guid tenant, Guid instance, string key, HumanSessionReference human,
        int page, int pageSize, CancellationToken ct = default)
    {
        if (!CoreEventCatalog.Keys.Contains(key)) throw new ArgumentException("Unknown built-in event source.");
        TenantWebhookRules.Page(page, pageSize);
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        await RequireAgent(state, human.PrincipalId, instance, ct).ConfigureAwait(false);
        var permission = PermissionCatalog.Get("core-events.read"); IReadOnlyList<Guid>? allowed = null;
        if (!(await iam.CheckTenantAsync(state, human.PrincipalId, permission, AccessScope.ForTenant(tenant), ct).ConfigureAwait(false)).Allowed)
        {
            var ids = new List<Guid>();
            foreach (var scope in state.Assignments.Where(a => a.PrincipalId == human.PrincipalId && a.TenantId == tenant
                && state.Roles.Any(r => r.RoleId == a.RoleId && r.Permissions.Contains(permission.Key))).Select(a => a.Scope).Distinct())
                if (TenantEventAdmissionRules.TryId(scope, out var id)
                    && (await iam.CheckOwnedTenantAsync(state, human.PrincipalId, permission, scope, ct).ConfigureAwait(false)).Allowed) ids.Add(id);
            allowed = ids;
        }
        var result = await events.ListTenantCoreEventsAsync(tenant, instance, key, page, pageSize, ct, allowed).ConfigureAwait(false);
        await Unchanged(state, human, ct).ConfigureAwait(false); return new(result, state.Tenant.Revision);
    }
    public async ValueTask<CoreEventReceipt> ReadAsync(Guid tenant, Guid eventId, HumanSessionReference human, CancellationToken ct = default)
    {
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        if (!(await iam.CheckOwnedTenantAsync(state, human.PrincipalId, PermissionCatalog.Get("core-events.read"), TenantEventAdmissionRules.Scope(tenant, eventId), ct).ConfigureAwait(false)).Allowed)
            throw new AccessDeniedException("event_source_unavailable");
        var source = await events.FindTenantCoreEventAsync(tenant, eventId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("event_source_unavailable");
        await Unchanged(state, human, ct).ConfigureAwait(false); return source;
    }
    private async ValueTask Unchanged(TenantSecurityState state, HumanSessionReference human, CancellationToken ct)
    {
        var current = await TenantHumanSessionAuthority.RequireAsync(security, identity, state.Tenant.TenantId, human, ct).ConfigureAwait(false);
        if (current.Tenant.Revision != state.Tenant.Revision) throw new SecurityConflictException("stale_revision");
    }
}
