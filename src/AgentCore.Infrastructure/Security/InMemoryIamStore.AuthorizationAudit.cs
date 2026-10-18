using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore : ITenantAuthorizationAuditStore
{
    public ValueTask<bool> TryRecordWorkspaceAuthorizationAsync(TenantWorkspaceHumanAuthorization proof, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); TenantWorkspaceMutationRules.Validate(proof);
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate)
        {
            if (!_tenants.TryGetValue(proof.Workspace.TenantId, out var state) || state.Tenant.Revision != proof.SecurityRevision) return ValueTask.FromResult(false);
            try { RequireHumanSession(proof.Workspace.TenantId, proof.Human); }
            catch (AccessDeniedException) { return ValueTask.FromResult(false); }
            var agent = FindTenantAgentAsync(proof.Workspace.TenantId, proof.Workspace.InstanceId, ct).Result;
            var session = proof.Workspace.SessionId is { } id ? sessionBacking.ReadTenantSession(proof.Workspace.TenantId, id) : null;
            if (agent is null || agent.Instance.Revision != proof.InstanceRevision || !TenantWorkspaceMutationRules.Matches(proof.Workspace, agent, session)) return ValueTask.FromResult(false);
            var allowed = TenantWorkspaceMutationRules.Writable(agent, session)
                && TenantWorkspaceMutationRules.Allowed(state, proof.Workspace, proof.Human.PrincipalId, proof.Permission, new IamAuthorizer(this), ct).GetAwaiter().GetResult();
            if (allowed != (proof.Audit.Decision == "allowed")) return ValueTask.FromResult(false);
            if (_audit.SingleOrDefault(a => a.AuditId == proof.Audit.AuditId) is { } existing) return ValueTask.FromResult(existing == proof.Audit);
            ct.ThrowIfCancellationRequested(); _audit.Add(proof.Audit); return ValueTask.FromResult(true);
        }
    }
    public ValueTask<bool> TryRecordOperationAuthorizationAsync(AuthorizationAudit audit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); TenantOperationAuditRules.Validate(audit); var e = audit.Execution!;
        lock (_gate) lock (sessionBacking.AdmissionGate)
        {
            if (!_tenants.TryGetValue(e.Context.TenantId, out var state)
                || sessionBacking.ReadTenantRun(e.Context.TenantId, e.Context.RunId) is not { } run
                || !TenantOperationAuditRules.Matches(audit, run, state.Tenant.Revision)) return ValueTask.FromResult(false);
            if (_audit.SingleOrDefault(a => a.AuditId == audit.AuditId) is { } existing) return ValueTask.FromResult(existing == audit);
            ct.ThrowIfCancellationRequested(); _audit.Add(audit); return ValueTask.FromResult(true);
        }
    }
}
