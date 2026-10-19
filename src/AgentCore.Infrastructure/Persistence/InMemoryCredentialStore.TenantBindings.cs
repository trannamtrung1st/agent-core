using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Credentials;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class InMemoryCredentialStore
{
    private readonly Dictionary<Guid, Guid> _tenantBindingOwners = [];
    internal void RetireTenantBindings(Guid tenant, Guid instance)
    {
        lock (instances.CredentialGate) foreach (var id in _bindings.Values.Where(b => b.AgentInstanceId == instance && _tenantBindingOwners.GetValueOrDefault(b.BindingId) == tenant).Select(b => b.BindingId).ToArray())
        { _bindings.Remove(id); _tenantBindingOwners.Remove(id); }
    }
    internal IReadOnlyList<TenantCredentialBinding> ReadTenantBindings(Guid tenant, Guid instance)
    {
        lock (instances.CredentialGate) return _bindings.Values.Where(b => b.AgentInstanceId == instance && _tenantBindingOwners.GetValueOrDefault(b.BindingId) == tenant)
            .OrderBy(b => b.Reference, StringComparer.Ordinal).Select(b => (Binding: b, Credential: ReadTenantCredential(tenant, b.CredentialId)))
            .Where(item => item.Credential is not null).Select(item => new TenantCredentialBinding(item.Binding, item.Credential!)).ToArray();
    }
    internal Action? PrepareTenantBind(Guid tenant, AgentCredentialBinding binding, long expectedCredentialRevision)
    {
        lock (instances.CredentialGate)
        {
            TenantCredentialBindingRules.NewBinding(binding);
            var credential = ReadTenantCredential(tenant, binding.CredentialId);
            if (credential is null || credential.Value.Revision != expectedCredentialRevision) return null;
            TenantCredentialBindingRules.Bindable(credential.Value);
            if (_bindings.ContainsKey(binding.BindingId) || _bindings.Values.Any(b => b.AgentInstanceId == binding.AgentInstanceId
                && (b.Reference == binding.Reference || b.CredentialId == binding.CredentialId))) throw new SecurityConflictException("credential_binding_conflict");
            return () => { _bindings.Add(binding.BindingId, binding); _tenantBindingOwners.Add(binding.BindingId, tenant); };
        }
    }
    internal Action? PrepareTenantUnbind(Guid tenant, Guid instance, Guid id, long revision)
    {
        lock (instances.CredentialGate)
        {
            if (_tenantBindingOwners.GetValueOrDefault(id) != tenant || !_bindings.TryGetValue(id, out var binding)
                || binding.AgentInstanceId != instance || binding.Revision != revision || !OwnsTenantCredential(tenant, binding.CredentialId)) return null;
            return () => { _bindings.Remove(id); _tenantBindingOwners.Remove(id); };
        }
    }
}
