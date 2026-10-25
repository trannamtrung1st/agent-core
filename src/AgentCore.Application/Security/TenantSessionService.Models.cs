using AgentCore.Application.Agents;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantSessionService
{
    // The API's existing Runtime host reserves a quiescent Session before this command.
    // Both stores repeat the no-active-Run and revision checks inside the owning transaction.
    public async ValueTask<TenantSession> SelectModelAsync(Guid tenant, Guid sessionId, TenantSessionInitiator human,
        long securityRevision, long sessionRevision, string? offeringId, string? reasoningEffort, CancellationToken ct = default)
    {
        var state = await Human(tenant, human, ct).ConfigureAwait(false);
        var current = await sessions.FindTenantSessionAsync(tenant, sessionId, ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("resource_unavailable");
        foreach (var permission in new[] { "sessions.read", "sessions.manage" })
            await Require(state, human.PrincipalId, permission, TenantSessionRules.Scope(tenant, sessionId), ct).ConfigureAwait(false);
        await Require(state, human.PrincipalId, "agents.chat", AccessScope.ForResource(tenant, "agent", current.Owner.AgentInstanceId.ToString("D")), ct).ConfigureAwait(false);
        var parent = await agents.FindTenantAgentAsync(tenant, current.Owner.AgentInstanceId, ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("resource_unavailable");
        TenantSessionRules.RequireExecutor(state, parent);
        if (parent.PrincipalId != current.Owner.ExecutorPrincipalId || parent.Instance.Lifecycle != AgentInstanceLifecycle.Active)
            throw new AccessDeniedException("executor_unavailable");
        if (state.Tenant.Revision != securityRevision || current.Snapshot.Revision != sessionRevision)
            throw new SecurityConflictException("stale_revision");
        var baseline = Guid.TryParse(parent.Instance.DefinitionId, out var definition)
            ? (await definitions.FindDefinitionPublicationAsync(tenant, definition, parent.Instance.ActiveVersion, ct).ConfigureAwait(false))?.Payload
            : await builtIns.GetAsync(parent.Instance.DefinitionId, parent.Instance.ActiveVersion, ct).ConfigureAwait(false);
        if (baseline is null) throw new AccessDeniedException("definition_unavailable");
        var effective = InstanceSettingsResolver.Resolve(baseline, parent.Instance.SettingsOverrides);
        var catalog = await modelCatalogs.ReadAsync(state, ct).ConfigureAwait(false);
        var model = SessionModelBinder.Bind(catalog, offeringId, reasoningEffort, ModelSelectionSource.User, effective.ModelDefaults);
        var change = new TenantSessionChange(sessionId, sessionRevision, TenantSessionAction.SelectModel, Model: model);
        var now = DateTimeOffset.FromUnixTimeMilliseconds(time.GetUtcNow().ToUnixTimeMilliseconds());
        _ = TenantSessionMutations.Apply(current.Snapshot, change, now);
        var next = state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } };
        var audit = new AuthorizationAudit(ids.NewId(), tenant, human.PrincipalId, TenantSessionMutations.AuditAction(change.Action), sessionId.ToString("D"), "allowed", "session_model_selected", now);
        return await sessions.TryChangeTenantSessionAsync(next, state.Tenant.Revision, change, human, now, audit, ct).ConfigureAwait(false)
            ?? throw new SecurityConflictException("stale_revision");
    }
}
