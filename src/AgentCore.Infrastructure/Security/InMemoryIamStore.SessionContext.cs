using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore : ITenantSessionContextStore
{
    private (TenantSession Session, TenantSecurityState Security, bool Replay) RequireContextSwitch(TenantContextSwitch request, bool live, CancellationToken ct)
    {
        if (request.Human is { } human) RequireHumanSession(request.Owner.TenantId, human);
        var state = _tenants.GetValueOrDefault(request.Owner.TenantId) ?? throw new AccessDeniedException("tenant_unavailable");
        var saved = sessionBacking.ReadTenantSession(request.Owner.TenantId, request.Owner.SessionId) ?? throw new AccessDeniedException("resource_unavailable");
        var agent = FindTenantAgentAsync(request.Owner.TenantId, request.Owner.AgentInstanceId, ct).Result ?? throw new AccessDeniedException("executor_unavailable");
        if (request.Agent is { } frame)
        {
            var run = sessionBacking.AgentRuns.Runs.GetValueOrDefault(frame.RunId) ?? throw new AccessDeniedException("run_unavailable");
            TenantSessionContextRules.RequireAgentAsync(request, saved, agent, run, TenantAgentDefinitionBaseline(agent), state,
                new IamAuthorizer(this), (clock ?? TimeProvider.System).GetUtcNow(), ct).AsTask().GetAwaiter().GetResult();
        }
        var destination = FindSpaceAsync(request.Owner.TenantId, request.SpaceId, ct).Result ?? throw new AccessDeniedException("space_unavailable");
        var member = _spaceMemberships.GetValueOrDefault((request.Owner.TenantId, request.SpaceId, request.Owner.AgentInstanceId));
        var environment = request.EnvironmentId is { } id ? FindEnvironmentAsync(request.Owner.TenantId, request.SpaceId, id, ct).Result : null;
        var replay = TenantSessionContextRules.IsReplay(request, saved.Snapshot.ContextSelection);
        var check = replay ? request with { ExpectedSecurityRevision = state.Tenant.Revision, ExpectedSessionRevision = saved.Snapshot.Revision,
            ExpectedSelectionRevision = saved.Snapshot.ContextSelection!.Revision, ExpectedInstanceRevision = agent.Instance.Revision } : request;
        TenantSessionContextRules.RequireAsync(check, saved, agent, state, new IamAuthorizer(this), destination, member, environment, live, ct).AsTask().GetAwaiter().GetResult();
        if (!replay)
        {
            var lease = sandboxBacking.TenantLease(request.Owner.TenantId, request.Owner.SessionId);
            if (lease is null && sandboxBacking.HasLease(request.Owner.SessionId)) throw new SecurityConflictException("sandbox_context_retirement_required");
            TenantSandboxContextRules.RequireRetired(lease);
        }
        return (saved, state, replay);
    }
    private void AuditContext(TenantContextSwitch request, TenantSecurityState state, Guid auditId, DateTimeOffset at)
    {
        if (auditId == Guid.Empty || _audit.Any(a => a.AuditId == auditId)) throw new ArgumentException("A unique audit identity is required.");
        _tenants[request.Owner.TenantId] = state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } };
        _audit.Add(new(auditId, request.Owner.TenantId, request.ActorPrincipalId, "sessions.context.switch", request.Owner.SessionId.ToString("D"), "allowed", "context_selection_changed", at));
    }
    public ValueTask<TenantContextSwitchResult> SwitchTenantQuiescentContextAsync(TenantContextSwitch request, Guid auditId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (sandboxBacking.TenantGate)
        {
            var (saved, state, replay) = RequireContextSwitch(request, false, ct);
            if (replay) return ValueTask.FromResult(new TenantContextSwitchResult(saved.Snapshot.ContextSelection!, saved.Snapshot.Revision, state.Tenant.Revision, "applied"));
            if (auditId == Guid.Empty || _audit.Any(a => a.AuditId == auditId)) throw new ArgumentException("A unique audit identity is required.");
            _ = checked(state.Tenant.Revision + 1);
            var at = (clock ?? TimeProvider.System).GetUtcNow(); var selection = TenantSessionContextRules.Selection(request, saved.Snapshot.ContextSelection!, null, at);
            var snapshot = sessionBacking.SwitchTenantQuiescentContext(request, selection, at);
            AuditContext(request, state, auditId, at);
            return ValueTask.FromResult(new TenantContextSwitchResult(selection, snapshot.Revision, checked(state.Tenant.Revision + 1), "applied"));
        }
    }
    public ValueTask<TenantContextSwitchResult> SaveTenantRuntimeContextAsync(TenantRuntimeFrame frame, SessionSnapshot snapshot, long expectedRevision,
        TenantContextSwitch request, Guid auditId, CancellationToken ct = default)
    {
        if (request.Agent is { } requestingFrame && requestingFrame != frame) throw new AccessDeniedException("tenant_context_mismatch");
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (sandboxBacking.TenantGate)
        {
            var (saved, state, replay) = RequireContextSwitch(request, true, ct);
            if (frame.SessionId != saved.Owner.SessionId || frame.Owner != AgentRunOwner.ForTenant(saved.Owner.TenantId, saved.Owner.AgentInstanceId, saved.Owner.ExecutorPrincipalId)) throw new AccessDeniedException("tenant_context_mismatch");
            if (replay) return ValueTask.FromResult(new TenantContextSwitchResult(saved.Snapshot.ContextSelection!, saved.Snapshot.Revision, state.Tenant.Revision, "pending_next_turn"));
            if (auditId == Guid.Empty || _audit.Any(a => a.AuditId == auditId)) throw new ArgumentException("A unique audit identity is required.");
            _ = checked(state.Tenant.Revision + 1);
            var at = snapshot.ContextSelection?.UpdatedAt ?? throw new AccessDeniedException("space_context_required");
            var selection = TenantSessionContextRules.Selection(request, saved.Snapshot.ContextSelection!, frame.RunId, at);
            if (snapshot.ContextSelection != selection) throw new AccessDeniedException("context_selection_changed");
            SaveTenantRuntimeState(frame, snapshot, expectedRevision, at, contextSwitch: true); AuditContext(request, state, auditId, at);
            return ValueTask.FromResult(new TenantContextSwitchResult(selection, snapshot.Revision, checked(state.Tenant.Revision + 1), "pending_next_turn"));
        }
    }
}
