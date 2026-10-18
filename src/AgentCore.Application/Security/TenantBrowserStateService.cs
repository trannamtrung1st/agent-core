using AgentCore.Application.Ports;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantBrowserStateView(Guid SessionId, ResourceStateObservation Observation, long SessionRevision, long SecurityRevision);

public sealed class TenantBrowserStateService(IIamStore security, ITenantSessionStore sessions, IdentityService identity,
    IamAuthorizer authorizer, IBrowser browser, TimeProvider time)
{
    public async ValueTask<TenantBrowserStateView> ReadAsync(Guid tenant, Guid session, HumanSessionReference human, CancellationToken ct = default)
    {
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        var saved = await sessions.FindTenantSessionAsync(tenant, session, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        await RequireRead(state, saved.Owner, human.PrincipalId, ct).ConfigureAwait(false);
        var owner = new TenantBrowserOwner(tenant, saved.Owner.AgentInstanceId, saved.Owner.ExecutorPrincipalId, session);
        var observation = browser is ITenantBrowserStateReader reader
            ? await reader.ObserveTenantSessionAsync(owner, ct).ConfigureAwait(false)
            : new("browser", "unavailable", "unknown", time.GetUtcNow(), Code: "provider_unavailable");
        var currentSession = await sessions.FindTenantSessionAsync(tenant, session, ct).ConfigureAwait(false);
        var current = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        await RequireRead(current, saved.Owner, human.PrincipalId, ct).ConfigureAwait(false);
        var final = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        if (currentSession?.Owner != saved.Owner || currentSession.Snapshot.Revision != saved.Snapshot.Revision
            || current.Tenant.Revision != state.Tenant.Revision || final.Tenant.Revision != state.Tenant.Revision)
            throw new SecurityConflictException("stale_revision");
        return new(session, observation, saved.Snapshot.Revision, state.Tenant.Revision);
    }
    private async ValueTask RequireRead(TenantSecurityState state, TenantSessionRoot owner, Guid actor, CancellationToken ct)
    {
        foreach (var target in new[] { ("sessions.read", TenantSessionRules.Scope(owner.TenantId, owner.SessionId)),
            ("browser.read", AccessScope.ForResource(owner.TenantId, "agent", owner.AgentInstanceId.ToString("D"))) })
        {
            var result = await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get(target.Item1), target.Item2, ct).ConfigureAwait(false);
            if (!result.Allowed) throw new AccessDeniedException(result.ReasonCode);
        }
    }
}
