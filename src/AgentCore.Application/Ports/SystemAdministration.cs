using AgentCore.Domain.Security;

namespace AgentCore.Application.Ports;

public sealed record SystemCollectionQuery(int Page = 0, int PageSize = 30, string? Search = null, IdentityStatus? Status = null);
public sealed record SystemAccountSummary(Guid AccountId, string LoginKey, string DisplayName, IdentityStatus Status, DateTimeOffset CreatedAt, string Version);
public sealed record SystemAccountPage(IReadOnlyList<SystemAccountSummary> Items, int Total, int Page, int PageSize);
public sealed record SystemTenantPage(IReadOnlyList<Tenant> Items, int Total, int Page, int PageSize);
public interface ISystemAdministrationStore
{
    ValueTask<SystemAccountPage> QuerySystemAccountsAsync(SystemCollectionQuery query, CancellationToken ct = default);
    ValueTask<SystemTenantPage> QuerySystemTenantsAsync(SystemCollectionQuery query, CancellationToken ct = default);
    ValueTask<bool> TryCreateSystemTenantAsync(HumanSessionReference human, TenantSecurityState tenant, AuthorizationAudit audit, CancellationToken ct = default);
    ValueTask<bool> TrySetSystemAccountStatusAsync(HumanSessionReference human, Guid accountId, string expectedVersion, IdentityStatus status,
        string nextSecurityStamp, AuthorizationAudit audit, CancellationToken ct = default);
    ValueTask<bool> TrySetSystemTenantStatusAsync(HumanSessionReference human, Guid tenantId, long expectedRevision, IdentityStatus status,
        AuthorizationAudit audit, CancellationToken ct = default);
}
