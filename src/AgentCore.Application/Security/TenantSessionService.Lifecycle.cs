using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantSessionService
{
    public async ValueTask<TenantSessionMetadata?> ChangeAsync(Guid tenant, TenantSessionInitiator initiator, TenantSessionChange change,
        long expectedSecurityRevision, CancellationToken ct = default)
    {
        if (change.Action == TenantSessionAction.SelectModel)
            throw AgentCoreErrors.Validation("Use the authorized model-selection command.");
        var current = await sessions.FindTenantSessionAsync(tenant, change.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var state = await Human(tenant, initiator, ct).ConfigureAwait(false); var scope = TenantSessionRules.Scope(tenant, change.SessionId);
        await Require(state, initiator.PrincipalId, TenantSessionMutations.Permission(change.Action), scope, ct).ConfigureAwait(false);
        if (change.Action != TenantSessionAction.Delete) await Require(state, initiator.PrincipalId, "sessions.read", scope, ct).ConfigureAwait(false);
        if (change.Action == TenantSessionAction.Resume)
        {
            var parent = await agents.FindTenantAgentAsync(tenant, current.Owner.AgentInstanceId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
            await Require(state, initiator.PrincipalId, "agents.chat", AccessScope.ForResource(tenant, "agent", current.Owner.AgentInstanceId.ToString("D")), ct).ConfigureAwait(false);
            if (parent.Instance.Lifecycle != AgentInstanceLifecycle.Active) throw new AccessDeniedException("agent_archived");
            TenantSessionRules.RequireExecutor(state, parent);
        }
        if (state.Tenant.Revision != expectedSecurityRevision || current.Snapshot.Revision != change.ExpectedSessionRevision) throw new SecurityConflictException("stale_revision");
        var now = DateTimeOffset.FromUnixTimeMilliseconds(time.GetUtcNow().ToUnixTimeMilliseconds());
        _ = TenantSessionMutations.Apply(current.Snapshot, change, now);
        var runIds = change.Action == TenantSessionAction.Delete ? (await runs.ListTenantRunIdsAsync(tenant, change.SessionId, ct).ConfigureAwait(false)).ToHashSet() : [];
        var artifactScopes = new HashSet<AccessScope>();
        if (change.Action == TenantSessionAction.Delete && artifacts is not null)
            foreach (var assignment in state.Assignments)
                if (TenantArtifactRules.TryId(assignment.Scope, out var artifact)
                    && await artifacts.FindTenantAsync(tenant, change.SessionId, artifact, ct).ConfigureAwait(false) is not null)
                    artifactScopes.Add(assignment.Scope);
        if (change.Action == TenantSessionAction.Delete && attachments is not null)
            foreach (var assignment in state.Assignments)
                if (TenantAttachmentRules.TryId(assignment.Scope, out var attachment) && await attachments.OwnsTenantAsync(tenant, change.SessionId, attachment, ct).ConfigureAwait(false))
                    artifactScopes.Add(assignment.Scope);
        var next = state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) },
            Assignments = change.Action == TenantSessionAction.Delete ? state.Assignments.Where(a => a.Scope != scope
                && !TenantWorkspaceRules.BelongsToSession(a.Scope, change.SessionId) && !artifactScopes.Contains(a.Scope)
                && !(TenantRunRules.TryId(a.Scope, out var runId) && runIds.Contains(runId))).ToArray() : state.Assignments };
        var audit = new AuthorizationAudit(ids.NewId(), tenant, initiator.PrincipalId, TenantSessionMutations.AuditAction(change.Action), change.SessionId.ToString("D"), "allowed", "session_changed", now);
        var saved = await sessions.TryChangeTenantSessionAsync(next, state.Tenant.Revision, change, initiator, now, audit, ct).ConfigureAwait(false) ?? throw new SecurityConflictException("stale_revision");
        if (browserRetirement is not null) await browserRetirement.ReconcileAsync(tenant, session: change.SessionId, ct: ct).ConfigureAwait(false);
        return change.Action == TenantSessionAction.Delete ? null : Metadata(saved, canManage: true);
    }
}
