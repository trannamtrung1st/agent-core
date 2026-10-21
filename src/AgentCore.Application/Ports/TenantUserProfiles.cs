using AgentCore.Domain.Security;

namespace AgentCore.Application.Ports;

public sealed record TenantUserProfileView(TenantUserProfile Profile, long SecurityRevision);
public sealed record TenantUserProfileChange(long ExpectedSecurityRevision, long ExpectedProfileRevision,
    IReadOnlyDictionary<string, string?> Values);

// Human self-service only. No arbitrary Profile/Account/Principal lookup, model fallback or implicit seed.
public interface ITenantUserProfileStore
{
    ValueTask<TenantUserProfileView> ReadOwnTenantUserProfileAsync(Guid tenant, HumanSessionReference human, CancellationToken ct = default);
    ValueTask<TenantUserProfileView?> TrySaveOwnTenantUserProfileAsync(TenantUserProfile profile, HumanSessionReference human,
        long expectedSecurityRevision, long expectedProfileRevision, AuthorizationAudit audit, CancellationToken ct = default);
}
