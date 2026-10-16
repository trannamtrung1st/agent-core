using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    public ValueTask<TenantSession?> FindTenantSessionAsync(Guid tenantId, Guid sessionId, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(sessionBacking.ReadTenantSession(tenantId, sessionId)); }
    public ValueTask<TenantSessionPage> ListTenantSessionsAsync(Guid tenantId, IReadOnlyList<Guid>? allowedIds, int page, int pageSize, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (tenantId == Guid.Empty || page < 0 || page > 100_000 || pageSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(page));
        lock (_gate) return ValueTask.FromResult(sessionBacking.ListTenantSessions(tenantId, allowedIds, page, pageSize));
    }
    public ValueTask<bool> TryCreateTenantSessionAsync(TenantSecurityState next, long expectedSecurityRevision, TenantSessionRoot owner,
        SessionSnapshot snapshot, TenantSessionInitiator initiator, long expectedInstanceRevision, AuthorizationAudit audit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); next = IamStateRules.Freeze(next);
        if (owner.TenantId != next.Tenant.TenantId || audit.TenantId != owner.TenantId || next.Tenant.Revision != checked(expectedSecurityRevision + 1)) throw new ArgumentException("Invalid tenant Session transaction.");
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate)
        {
            if (!_tenants.TryGetValue(owner.TenantId, out var before) || before.Tenant.Revision != expectedSecurityRevision) return ValueTask.FromResult(false);
            IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); ValidateAgentActor(before, audit.ActorPrincipalId); EnsureReferences(next, audit, ct: ct);
            RequireHumanSession(owner.TenantId, initiator);
            var agent = FindTenantAgentAsync(owner.TenantId, owner.AgentInstanceId, ct).Result;
            if (agent is null || agent.Instance.Revision != expectedInstanceRevision) return ValueTask.FromResult(false);
            TenantSessionRules.RequireExecutor(before, agent);
            TenantSessionRules.ValidateCreation(owner, snapshot, agent, initiator, audit);
            ct.ThrowIfCancellationRequested(); if (!sessionBacking.CreateTenantSession(owner, snapshot)) return ValueTask.FromResult(false);
            _tenants[owner.TenantId] = next; _audit.Add(audit); return ValueTask.FromResult(true);
        }
    }
}
