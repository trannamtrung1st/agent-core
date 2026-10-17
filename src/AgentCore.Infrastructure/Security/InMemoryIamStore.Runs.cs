using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    public ValueTask<TenantSessionRoot?> ResolveTenantSessionOwnerAsync(Guid sessionId, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(sessionBacking.ResolveTenantSessionOwner(sessionId)); }
    public ValueTask<AgentRun?> TryCancelTenantRunAsync(TenantSecurityState next, long expectedSecurityRevision, Guid runId, long expectedRunRevision,
        TenantSessionInitiator initiator, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); next = IamStateRules.Freeze(next);
        if (next.Tenant.Revision != checked(expectedSecurityRevision + 1) || audit.TenantId != next.Tenant.TenantId || audit.ActorPrincipalId != initiator.PrincipalId
            || audit.Action != "agent-runs.cancel" || audit.Target != runId.ToString("D")) throw new ArgumentException("Invalid Run cancellation transaction.");
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate)
        {
            if (!_tenants.TryGetValue(next.Tenant.TenantId, out var before) || before.Tenant.Revision != expectedSecurityRevision) return ValueTask.FromResult<AgentRun?>(null);
            IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); ValidateAgentActor(before, initiator.PrincipalId); EnsureReferences(next, audit, ct: ct);
            RequireHumanSession(next.Tenant.TenantId, initiator); ct.ThrowIfCancellationRequested();
            var saved = sessionBacking.CancelTenantRun(next.Tenant.TenantId, runId, expectedRunRevision, at);
            if (saved is not null) { _tenants[next.Tenant.TenantId] = next; _audit.Add(audit); }
            return ValueTask.FromResult(saved);
        }
    }
    public ValueTask<AgentRun?> FindTenantRunAsync(Guid tenantId, Guid runId, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(sessionBacking.ReadTenantRun(tenantId, runId)); }
    public ValueTask<IReadOnlyList<Guid>> ListTenantRunIdsAsync(Guid tenantId, Guid sessionId, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(sessionBacking.TenantRunIds(tenantId, sessionId)); }
    public ValueTask<TenantRunAdmissionResult?> TryAdmitTenantHumanRunAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantHumanRunAdmission admission, TenantSessionInitiator initiator, AuthorizationAudit audit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); next = IamStateRules.Freeze(next); var run = admission.Run;
        if (run.Owner.TenantId != next.Tenant.TenantId || next.Tenant.Revision != checked(expectedSecurityRevision + 1)
            || audit.TenantId != next.Tenant.TenantId || audit.ActorPrincipalId != initiator.PrincipalId
            || audit.Action != "agent-runs.admit" || audit.Target != run.AgentRunId.ToString("D")) throw new ArgumentException("Invalid Run admission transaction.");
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate)
        {
            if (!_tenants.TryGetValue(next.Tenant.TenantId, out var before) || before.Tenant.Revision != expectedSecurityRevision) return ValueTask.FromResult<TenantRunAdmissionResult?>(null);
            IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next);
            ValidateAgentActor(before, initiator.PrincipalId); EnsureReferences(next, audit, ct: ct); RequireHumanSession(next.Tenant.TenantId, initiator);
            var agent = FindTenantAgentAsync(next.Tenant.TenantId, run.AgentInstanceId, ct).Result;
            if (agent is null || agent.Instance.Revision != admission.ExpectedInstanceRevision) return ValueTask.FromResult<TenantRunAdmissionResult?>(null);
            if (agent.Instance.Lifecycle != AgentCore.Domain.Definitions.AgentInstanceLifecycle.Active) throw new AccessDeniedException("agent_archived");
            TenantSessionRules.RequireExecutor(before, agent); ct.ThrowIfCancellationRequested();
            var result = sessionBacking.AdmitTenantRun(admission, agent, initiator);
            if (result?.Created == true) { _tenants[next.Tenant.TenantId] = next; _audit.Add(audit); }
            return ValueTask.FromResult(result);
        }
    }
}
