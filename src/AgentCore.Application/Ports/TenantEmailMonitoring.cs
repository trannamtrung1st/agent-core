using AgentCore.Domain.Credentials;
using AgentCore.Domain.Email;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Ports;

// The watch is an owned account source. It carries neither a fictitious Agent
// nor the configuring human's reusable login/permission authority.
public sealed record TenantEmailWatchClaim(Guid TenantId, EmailWatchState State, Credential Account);
public sealed record TenantEmailWatchChange(HumanSessionReference Human, long CredentialRevision, long WatchRevision,
    bool CheckNow, bool Enabled = false, int IntervalSeconds = 300);

public interface ITenantEmailWatchStore
{
    ValueTask<EmailWatchState?> ReadTenantEmailWatchAsync(Guid tenantId, Guid accountId, CancellationToken ct = default);
    ValueTask<bool> TryChangeTenantEmailWatchAsync(TenantSecurityState next, long expectedSecurityRevision,
        Guid accountId, TenantEmailWatchChange change, AuthorizationAudit audit, DateTimeOffset now, CancellationToken ct = default);
    ValueTask<IReadOnlyList<TenantEmailWatchClaim>> ClaimDueTenantEmailWatchesAsync(DateTimeOffset now, int limit, CancellationToken ct = default);
    ValueTask<Credential?> RecheckTenantEmailWatchAsync(TenantEmailWatchClaim claim, DateTimeOffset now, CancellationToken ct = default);
    // The owner derives all eligible bindings, actual executor identities and
    // complete subscription snapshots inside this cursor/receipt transaction.
    ValueTask<bool> TryCommitTenantEmailWatchAsync(TenantEmailWatchClaim claim, EmailWatchBatch batch, DateTimeOffset now, CancellationToken ct = default);
    ValueTask RecordTenantEmailWatchFailureAsync(TenantEmailWatchClaim claim, SafeEmailWatchFailure failure, DateTimeOffset now, CancellationToken ct = default);
    ValueTask<bool> TryMaintainTenantEmailWatchAsync(TenantEmailWatchClaim claim, long expectedSecretRevision,
        CredentialOAuthCheck maintenance, DateTimeOffset now, CancellationToken ct = default);
    ValueTask<bool> CanReadRetainedTenantEmailEventAsync(Guid tenantId, Guid eventId, CancellationToken ct = default);
    ValueTask<bool> CanDeliverTenantEmailEventAsync(Guid tenantId, Guid eventId, CancellationToken ct = default);
}

public interface ITenantEmailChangeFeed
{
    bool IsSynthetic { get; }
    bool HostAvailable { get; }
    ConnectedAccountAvailability WatchAvailability(Credential account);
    ValueTask<EmailWatchBatch> ReadTenantChangesAsync(Guid tenantId, Credential account, EmailWatchCursor? cursor,
        Func<CancellationToken, ValueTask<Credential>> recheck,
        Func<CredentialOAuthCheck, CancellationToken, ValueTask> maintenance, CancellationToken ct = default);
}
