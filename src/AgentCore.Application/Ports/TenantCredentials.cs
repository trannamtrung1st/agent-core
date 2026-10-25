using AgentCore.Domain.Credentials;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Ports;

public sealed record TenantCredentialRoot(Guid TenantId, Guid CredentialId, DateTimeOffset CreatedAt, DateTimeOffset? DeletedAt = null);
public sealed record TenantCredential(TenantCredentialRoot Owner, Credential Value, int BindingCount);
public sealed record TenantCredentialPage(IReadOnlyList<TenantCredential> Items, int Total, int Page, int PageSize);

// The existing protected row, its actual owner, security CAS and audit share one transaction.
// This port exposes no plaintext secret or runtime-use operation.
public interface ITenantCredentialStore
{
    ValueTask<TenantCredential?> FindTenantCredentialAsync(Guid tenantId, Guid credentialId, CancellationToken ct = default);
    ValueTask<TenantCredentialPage> ListTenantCredentialsAsync(Guid tenantId, IReadOnlyList<Guid>? allowedIds, int page, int pageSize, CancellationToken ct = default);
    ValueTask<IReadOnlyDictionary<Guid, DateTimeOffset>> ReadTenantCredentialRotationsAsync(Guid tenantId,
        IReadOnlyList<Guid> credentialIds, CancellationToken ct = default);
    ValueTask<bool> TrySaveTenantCredentialAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantCredentialRoot owner, Credential value, long expectedRevision, long expectedSecretRevision,
        AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null);
    ValueTask<bool> TryDeleteTenantCredentialAsync(TenantSecurityState next, long expectedSecurityRevision,
        Guid credentialId, long expectedRevision, long expectedSecretRevision, DateTimeOffset at,
        AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null);
}
