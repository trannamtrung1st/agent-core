using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    public ValueTask<TenantCredential?> FindTenantCredentialAsync(Guid tenantId, Guid credentialId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (instances.CredentialGate)
            return ValueTask.FromResult(CredentialBacking().ReadTenantCredential(tenantId, credentialId));
    }
    public ValueTask<TenantCredentialPage> ListTenantCredentialsAsync(Guid tenantId, IReadOnlyList<Guid>? allowedIds, int page, int pageSize, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (tenantId == Guid.Empty || page < 0 || page > 100_000 || pageSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(page));
        lock (_gate) lock (instances.CredentialGate) return ValueTask.FromResult(CredentialBacking().ListTenantCredentials(tenantId, allowedIds, page, pageSize));
    }
    public ValueTask<bool> TrySaveTenantCredentialAsync(TenantSecurityState next, long expectedSecurityRevision, TenantCredentialRoot owner,
        Credential value, long expectedRevision, long expectedSecretRevision, AuthorizationAudit audit, CancellationToken ct = default)
    {
        next = IamStateRules.Freeze(next); value = TenantCredentialRules.Freeze(value);
        return ValueTask.FromResult(WriteCredential(next, expectedSecurityRevision, audit, () => owner.TenantId == next.Tenant.TenantId
            && CredentialBacking().SaveTenantCredential(owner, value, expectedRevision, expectedSecretRevision), ct));
    }
    public ValueTask<bool> TryDeleteTenantCredentialAsync(TenantSecurityState next, long expectedSecurityRevision, Guid credentialId,
        long expectedRevision, long expectedSecretRevision, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default)
    {
        next = IamStateRules.Freeze(next);
        return ValueTask.FromResult(WriteCredential(next, expectedSecurityRevision, audit, () =>
        {
            if (next.Assignments.Any(a => a.Scope == TenantCredentialRules.Scope(next.Tenant.TenantId, credentialId)))
                throw new ArgumentException("Deleting a credential must retire its direct assignments.");
            return CredentialBacking().DeleteTenantCredential(next.Tenant.TenantId, credentialId, expectedRevision, expectedSecretRevision, at);
        }, ct));
    }
    private AgentCore.Infrastructure.Persistence.InMemoryCredentialStore CredentialBacking() => credentials ?? throw new AccessDeniedException("credential_store_unavailable");
    private bool WriteCredential(TenantSecurityState next, long expected, AuthorizationAudit audit, Func<bool> change, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); next = IamStateRules.Freeze(next);
        if (audit.TenantId != next.Tenant.TenantId || next.Tenant.Revision != checked(expected + 1)) throw new ArgumentException("Invalid credential transaction.");
        lock (_gate) lock (instances.CredentialGate)
        {
            if (!_tenants.TryGetValue(next.Tenant.TenantId, out var before) || before.Tenant.Revision != expected) return false;
            IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); ValidateAgentActor(before, audit.ActorPrincipalId); EnsureReferences(next, audit, ct: ct);
            ct.ThrowIfCancellationRequested(); if (!change()) return false;
            _tenants[next.Tenant.TenantId] = next; _audit.Add(audit); return true;
        }
    }
}
