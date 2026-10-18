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
        var tenant = next.Tenant.TenantId; TenantSandboxControlRules.Audit(tenant, reset, human, audit);
        if (next.Tenant.Revision != checked(expectedSecurityRevision + 1)) throw new ArgumentException("Invalid Sandbox reset transaction.");
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (sandboxBacking.TenantGate)
        {
            if (!_tenants.TryGetValue(tenant, out var before) || before.Tenant.Revision != expectedSecurityRevision) return ValueTask.FromResult<SandboxLease?>(null);
            RequireAgentHuman(tenant, human.PrincipalId, human);
            IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); ValidateAgentActor(before, human.PrincipalId); EnsureReferences(next, audit, ct: ct);
            var session = sessionBacking.ReadTenantSession(tenant, reset.SessionId) ?? throw new AccessDeniedException("resource_unavailable");
            var lease = sandboxBacking.TenantLease(tenant, reset.SessionId) ?? throw new AccessDeniedException("resource_unavailable");
            TenantSandboxControlRules.RequirePermissionsAsync(before, session.Owner, lease.Profile.Id, human, new IamAuthorizer(this), ct).GetAwaiter().GetResult();
            TenantSandboxControlRules.Require(reset, session, lease);
            ct.ThrowIfCancellationRequested(); var requested = TenantSandboxControlRules.Apply(lease);
            sandboxBacking.SaveTenantLease(requested); _tenants[tenant] = next; _audit.Add(audit);
            return ValueTask.FromResult<SandboxLease?>(requested);
        }
    }
    public ValueTask<TenantSandboxRuntimeSnapshot?> TryRequestTenantSandboxProcessStopAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantSandboxProcessStop stop, HumanSessionReference human, AuthorizationAudit audit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); next = IamStateRules.Freeze(next); var tenant = next.Tenant.TenantId;
        TenantSandboxControlRules.Audit(tenant, stop, human, audit);
        if (next.Tenant.Revision != checked(expectedSecurityRevision + 1)) throw new ArgumentException("Invalid Sandbox Stop transaction.");
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (sandboxBacking.TenantGate)
        {
            if (!_tenants.TryGetValue(tenant, out var before) || before.Tenant.Revision != expectedSecurityRevision) return ValueTask.FromResult<TenantSandboxRuntimeSnapshot?>(null);
            RequireAgentHuman(tenant, human.PrincipalId, human);
            IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); ValidateAgentActor(before, human.PrincipalId); EnsureReferences(next, audit, ct: ct);
            var session = sessionBacking.ReadTenantSession(tenant, stop.SessionId) ?? throw new AccessDeniedException("resource_unavailable");
            var lease = sandboxBacking.TenantLease(tenant, stop.SessionId) ?? throw new AccessDeniedException("resource_unavailable");
            var execution = sandboxBacking.TenantExecution(tenant, stop.SessionId, stop.ExecutionId) ?? throw new AccessDeniedException("resource_unavailable");
            TenantSandboxControlRules.RequirePermissionsAsync(before, session.Owner, lease.Profile.Id, human, new IamAuthorizer(this), ct).GetAwaiter().GetResult();
            TenantSandboxControlRules.Require(stop, session, lease, execution);
            var requested = TenantSandboxControlRules.ApplyStop(execution, audit);
            var pending = lease with { Revision = checked(lease.Revision + 1), SafeMessage = "Stop requested. Process cleanup is pending; canonical files are retained." };
            ct.ThrowIfCancellationRequested(); sandboxBacking.SaveTenantExecution(requested); sandboxBacking.SaveTenantLease(pending);
            _tenants[tenant] = next; _audit.Add(audit);
            return ValueTask.FromResult<TenantSandboxRuntimeSnapshot?>(new(session.Owner, session.Snapshot.Revision, pending, requested));
        }
    }
    public ValueTask<SandboxExecution?> TryStartTenantSandboxProcessStopAsync(Guid tenant, Guid session, Guid execution, Guid stopRequestId,
        long expectedExecutionRevision, long expectedLeaseRevision, DateTimeOffset at, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (sandboxBacking.TenantGate)
        {
            var root = sessionBacking.ResolveTenantSessionOwner(session); var lease = sandboxBacking.TenantLease(tenant, session);
            var receipt = sandboxBacking.TenantExecution(tenant, session, execution);
            if (root?.TenantId != tenant || lease is null || receipt is null || root.ExecutorPrincipalId != receipt.ExecutorPrincipalId) return ValueTask.FromResult<SandboxExecution?>(null);
            var claimed = TenantSandboxControlRules.StartStop(lease, receipt, stopRequestId, expectedExecutionRevision, expectedLeaseRevision, at);
            if (claimed is not null) sandboxBacking.SaveTenantExecution(claimed);
            return ValueTask.FromResult(claimed);
        }
    }

}
