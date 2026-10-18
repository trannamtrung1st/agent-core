using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    public ValueTask<SandboxLease?> TryRequestTenantSandboxResetAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantSandboxReset reset, HumanSessionReference human, AuthorizationAudit audit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); next = IamStateRules.Freeze(next);
        var tenant = next.Tenant.TenantId; TenantSandboxResetRules.Audit(tenant, reset, human, audit);
        if (next.Tenant.Revision != checked(expectedSecurityRevision + 1)) throw new ArgumentException("Invalid Sandbox reset transaction.");
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (sandboxBacking.TenantGate)
        {
            if (!_tenants.TryGetValue(tenant, out var before) || before.Tenant.Revision != expectedSecurityRevision) return ValueTask.FromResult<SandboxLease?>(null);
            RequireAgentHuman(tenant, human.PrincipalId, human);
            IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); ValidateAgentActor(before, human.PrincipalId); EnsureReferences(next, audit, ct: ct);
            var session = sessionBacking.ReadTenantSession(tenant, reset.SessionId) ?? throw new AccessDeniedException("resource_unavailable");
            var lease = sandboxBacking.TenantLease(tenant, reset.SessionId) ?? throw new AccessDeniedException("resource_unavailable");
            TenantSandboxResetRules.RequirePermissionsAsync(before, session.Owner, lease.Profile.Id, human, new IamAuthorizer(this), ct).GetAwaiter().GetResult();
            TenantSandboxResetRules.Require(reset, session, lease);
            ct.ThrowIfCancellationRequested(); var requested = TenantSandboxResetRules.Apply(lease);
            sandboxBacking.SaveTenantLease(requested); _tenants[tenant] = next; _audit.Add(audit);
            return ValueTask.FromResult<SandboxLease?>(requested);
        }
    }
}
