using AgentCore.Domain.Security;

namespace AgentCore.Application.Ports;

public interface IIamStore
{
    // Resolves supported external resource families through their actual owning stores.
    ValueTask<bool> OwnsResourceAsync(AccessScope scope, CancellationToken ct = default);
    ValueTask<Account?> FindAccountAsync(string loginKey, CancellationToken ct = default);
    ValueTask<Account?> ReadAccountAsync(Guid accountId, CancellationToken ct = default);
    ValueTask<bool> HasBootstrapAsync(CancellationToken ct = default);
    // One transaction: first Account, platform Principal/roles and initial Tenant/Owner; one-use globally.
    ValueTask<bool> TryBootstrapAsync(Account account, SystemSecurityState system,
        TenantSecurityState tenant, AuthorizationAudit audit, CancellationToken ct = default);
    ValueTask<bool> TryCreateAccountAsync(Account account, Principal accountPrincipal, CancellationToken ct = default);
    // Account changes and last-active-Owner checks serialize with tenant security writes.
    ValueTask<bool> TryUpdateAccountAsync(Account account, string expectedSecurityStamp,
        AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null);
    ValueTask<SystemSecurityState> ReadSystemAsync(CancellationToken ct = default);
    ValueTask<TenantSecurityState?> ReadTenantAsync(Guid tenantId, CancellationToken ct = default);
    ValueTask<IReadOnlyList<TenantMembership>> ListMembershipsAsync(Guid accountId, CancellationToken ct = default);
    ValueTask<bool> TryCreateTenantAsync(TenantSecurityState tenant, AuthorizationAudit audit, CancellationToken ct = default);
    ValueTask<bool> TrySaveTenantAsync(TenantSecurityState tenant, long expectedRevision,
        AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null);
    ValueTask SaveSessionAsync(AuthenticationSession session, CancellationToken ct = default);
    ValueTask<AuthenticationSession?> ReadSessionAsync(Guid sessionId, CancellationToken ct = default);
    ValueTask RevokeSessionAsync(Guid sessionId, DateTimeOffset now, CancellationToken ct = default);
    ValueTask<IReadOnlyList<AuthorizationAudit>> ReadAuditAsync(Guid tenantId, int limit, CancellationToken ct = default);
}

// Vetted password hashing belongs in Infrastructure; no authentication framework crosses this port.
public interface IAccountPasswordHasher
{
    string Hash(Account account, string password);
    bool Verify(Account account, string password);
}

public interface IIdentityTokens
{
    string Create();
    string Hash(string token);
    bool Matches(string token, string hash);
}
