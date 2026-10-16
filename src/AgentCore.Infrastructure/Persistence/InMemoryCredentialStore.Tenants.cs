using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Credentials;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class InMemoryCredentialStore
{
    private readonly Dictionary<Guid, TenantCredentialRoot> _tenantOwners = [];
    internal bool OwnsTenantCredential(Guid tenant, Guid id)
    {
        lock (instances.CredentialGate) return _tenantOwners.GetValueOrDefault(id) is { DeletedAt: null } root
            && root.TenantId == tenant && _credentials.ContainsKey(id);
    }
    internal TenantCredential? ReadTenantCredential(Guid tenant, Guid id)
    {
        lock (instances.CredentialGate) return OwnsTenantCredential(tenant, id)
            ? new(_tenantOwners[id], TenantCredentialRules.Freeze(_credentials[id]), _bindings.Values.Count(b => b.CredentialId == id)) : null;
    }
    internal TenantCredentialPage ListTenantCredentials(Guid tenant, IReadOnlyList<Guid>? allowedIds, int page, int pageSize)
    {
        lock (instances.CredentialGate)
        {
            var allowed = allowedIds?.ToHashSet();
            var roots = _tenantOwners.Values.Where(r => r.TenantId == tenant && r.DeletedAt is null && _credentials.ContainsKey(r.CredentialId)
                && (allowed is null || allowed.Contains(r.CredentialId))).OrderBy(r => r.CredentialId.ToString("D"), StringComparer.Ordinal).ToArray();
            return new(roots.Skip(page * pageSize).Take(pageSize).Select(r => ReadTenantCredential(tenant, r.CredentialId)!).ToArray(), roots.Length, page, pageSize);
        }
    }
    internal bool SaveTenantCredential(TenantCredentialRoot owner, Credential value, long expectedRevision, long expectedSecretRevision)
    {
        lock (instances.CredentialGate)
        {
            var root = _tenantOwners.GetValueOrDefault(owner.CredentialId); var current = _credentials.GetValueOrDefault(owner.CredentialId);
            if (root is null ? expectedRevision != 0 || current is not null : root != owner || root.DeletedAt is not null || current is null) return false;
            if ((current?.Revision ?? 0) != expectedRevision || (current?.SecretRevision ?? 0) != expectedSecretRevision) return false;
            value = TenantCredentialRules.Freeze(value); TenantCredentialRules.ValidateChange(owner, value, current, expectedRevision, expectedSecretRevision);
            _credentials[owner.CredentialId] = value; _tenantOwners[owner.CredentialId] = owner; return true;
        }
    }
    internal bool DeleteTenantCredential(Guid tenant, Guid id, long expectedRevision, long expectedSecretRevision, DateTimeOffset at)
    {
        lock (instances.CredentialGate)
        {
            if (!OwnsTenantCredential(tenant, id)) return false;
            var value = _credentials[id];
            if (value.Revision != expectedRevision || value.SecretRevision != expectedSecretRevision) return false;
            if (_bindings.Values.Any(b => b.CredentialId == id)) throw new SecurityConflictException("credential_in_use");
            _credentials.Remove(id); _tenantOwners[id] = _tenantOwners[id] with { DeletedAt = at }; return true;
        }
    }
}
