using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed class TenantSessionForegroundService(IIamStore security, ITenantSessionStore sessions, ITenantAgentStore agents,
    IdentityService identity, IamAuthorizer authorizer, ITenantSessionForegroundControl control)
{
    public async ValueTask<TenantSessionForeground> PrepareAsync(Guid tenant, Guid sessionId, TenantSessionInitiator human,
        long securityRevision, long sessionRevision, CancellationToken ct = default)
    {
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        var session = await sessions.FindTenantSessionAsync(tenant, sessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var agent = await agents.FindTenantAgentAsync(tenant, session.Owner.AgentInstanceId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var request = new TenantSessionForeground(session.Owner, human, securityRevision, sessionRevision, agent.Instance.Revision);
        await TenantSessionForegroundRules.RequireAsync(request, session, agent, state, authorizer, live: false, ct).ConfigureAwait(false);
        return request;
    }
    public async ValueTask ContinueAsync(Guid tenant, Guid sessionId, TenantSessionInitiator human,
        long securityRevision, long sessionRevision, CancellationToken ct = default)
    {
        var request = await PrepareAsync(tenant, sessionId, human, securityRevision, sessionRevision, ct).ConfigureAwait(false);
        await control.ContinueAsync(request, ct).ConfigureAwait(false);
    }
}

public static class TenantSessionForegroundRules
{
    public static async ValueTask RequireAsync(TenantSessionForeground request, TenantSession saved, TenantAgent agent,
        TenantSecurityState state, IamAuthorizer authorizer, bool live, CancellationToken ct)
    {
        if (saved.Owner != request.Owner || state.Tenant.TenantId != request.Owner.TenantId
            || agent.TenantId != request.Owner.TenantId || agent.PrincipalId != request.Owner.ExecutorPrincipalId
            || agent.Instance.InstanceId != request.Owner.AgentInstanceId) throw new AccessDeniedException("resource_unavailable");
        foreach (var (permission, scope) in new[] {
            ("sessions.read", TenantSessionRules.Scope(request.Owner.TenantId, request.Owner.SessionId)),
            ("sessions.manage", TenantSessionRules.Scope(request.Owner.TenantId, request.Owner.SessionId)),
            ("agents.chat", AccessScope.ForResource(request.Owner.TenantId, "agent", request.Owner.AgentInstanceId.ToString("D"))) })
        {
            var decision = await authorizer.CheckTenantAsync(state, request.Human.PrincipalId, PermissionCatalog.Get(permission), scope, ct).ConfigureAwait(false);
            if (!decision.Allowed) throw new AccessDeniedException(decision.ReasonCode);
        }
        TenantSessionRules.RequireExecutor(state, agent);
        if (agent.Instance.Lifecycle != AgentInstanceLifecycle.Active) throw new AccessDeniedException("agent_archived");
        if (state.Tenant.Revision != request.SecurityRevision || agent.Instance.Revision != request.InstanceRevision
            || (live ? saved.Snapshot.Revision < request.ObservedSessionRevision : saved.Snapshot.Revision != request.ObservedSessionRevision))
            throw new SecurityConflictException("stale_revision");
        Eligible(saved.Snapshot);
    }
    public static void Eligible(SessionSnapshot snapshot)
    {
        if (!snapshot.Surfaces.HasFlag(SessionSurface.BackgroundWork) || snapshot.DurablyDeletedAt is not null || snapshot.ArchivedAt is not null
            || SessionLifecycle.IsTerminal(snapshot.LifecycleStatus) || snapshot.Status is SessionStatus.Ended or SessionStatus.Ending)
            throw new AccessDeniedException("session_unavailable");
    }
}
