using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore : ITenantConnectedAccountReadStore
{
    public ValueTask<bool> TryRecordConnectedAccountReadAsync(TenantConnectedAccountReadProof proof, CancellationToken ct = default)
    {
        TenantConnectedAccountRules.Proof(proof); ct.ThrowIfCancellationRequested();
        lock (_gate) lock (instances.CredentialGate)
        {
            if (!_tenants.TryGetValue(proof.TenantId, out var state) || state.Tenant.Revision != proof.SecurityRevision) return ValueTask.FromResult(false);
            try { RequireHumanSession(proof.TenantId, proof.Human); }
            catch (AccessDeniedException) { return ValueTask.FromResult(false); }
            var snapshot = ReadTenantCredentialBindingsAsync(proof.TenantId, proof.InstanceId, ct).Result;
            var bound = snapshot?.Bindings.SingleOrDefault(b => b.Binding.BindingId == proof.BindingId && b.Binding.Revision == proof.BindingRevision);
            if (snapshot is null || snapshot.Agent.Instance.Revision != proof.InstanceRevision || snapshot.Agent.Instance.Lifecycle != Domain.Definitions.AgentInstanceLifecycle.Active || bound is null
                || bound.Credential.Value.CredentialId != proof.CredentialId || bound.Credential.Value.Revision != proof.CredentialRevision
                || bound.Credential.Value.SecretRevision != proof.SecretRevision || bound.Credential.Value.Purpose != proof.Purpose
                || bound.Credential.Value.Status != Domain.Credentials.CredentialStatus.Active) return ValueTask.FromResult(false);
            var iam = new IamAuthorizer(this);
            try { TenantSessionRules.RequireExecutor(state, snapshot.Agent); }
            catch (AccessDeniedException) { return ValueTask.FromResult(false); }
            if (!TenantConnectedAccountRules.AgentReadAsync(state, proof.InstanceId, proof.Human.PrincipalId, iam, ct).Result
                || !TenantConnectedAccountRules.AllowedAsync(state, bound.Credential.Value, proof.Human.PrincipalId, iam, true, ct).Result)
                return ValueTask.FromResult(false);
            var prior = _audit.SingleOrDefault(a => a.AuditId == proof.Audit.AuditId);
            if (prior is not null) return ValueTask.FromResult(prior == proof.Audit);
            ct.ThrowIfCancellationRequested(); _audit.Add(proof.Audit); return ValueTask.FromResult(true);
        }
    }
}
