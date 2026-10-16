using AgentCore.Domain.Credentials;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Ports;

public sealed record TenantCredentialBinding(AgentCredentialBinding Binding, TenantCredential Credential);
public sealed record TenantCredentialBindingSnapshot(TenantAgent Agent, IReadOnlyList<TenantCredentialBinding> Bindings);

// Association configuration only. It grants no secret-use or downstream effect authority.
public interface ITenantCredentialBindingStore
{
    ValueTask<TenantCredentialBindingSnapshot?> ReadTenantCredentialBindingsAsync(Guid tenantId, Guid instanceId, CancellationToken ct = default);
    ValueTask<TenantAgent?> TryBindTenantCredentialAsync(TenantSecurityState next, long expectedSecurityRevision,
        AgentCredentialBinding binding, long expectedInstanceRevision, long expectedCredentialRevision,
        AuthorizationAudit audit, CancellationToken ct = default);
    ValueTask<TenantAgent?> TryUnbindTenantCredentialAsync(TenantSecurityState next, long expectedSecurityRevision,
        Guid instanceId, Guid bindingId, long expectedInstanceRevision, long expectedBindingRevision, DateTimeOffset at,
        AuthorizationAudit audit, CancellationToken ct = default);
}
