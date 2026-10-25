using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    public ValueTask<bool> TrySaveTenantOperationPolicyAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantSessionInitiator initiator, AuthorizationAudit audit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); next = IamStateRules.Freeze(next);
        if (audit.ActorPrincipalId != initiator.PrincipalId || audit.TenantId != next.Tenant.TenantId || audit.Action is not ("tenant.operation_policy.update" or "tenant.chat_retention.update")
            || next.Tenant.Revision != checked(expectedSecurityRevision + 1)) throw new ArgumentException("Invalid operation policy transaction.");
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate)
        {
            if (!_tenants.TryGetValue(next.Tenant.TenantId, out var before) || before.Tenant.Revision != expectedSecurityRevision) return ValueTask.FromResult(false);
            IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); ValidateAgentActor(before, initiator.PrincipalId); EnsureReferences(next, audit, ct: ct);
            RequireHumanSession(next.Tenant.TenantId, initiator); ct.ThrowIfCancellationRequested();
            _tenants[next.Tenant.TenantId] = next; _audit.Add(audit); return ValueTask.FromResult(true);
        }
    }
}
