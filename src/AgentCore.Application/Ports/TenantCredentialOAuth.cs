using AgentCore.Domain.Credentials;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Ports;

public enum CredentialOAuthOutcome { Pending, Claimed, Connected, Denied, Failed, Expired, Superseded }
public sealed record CredentialOAuthInitiator(Guid AccountId, Guid AuthenticationSessionId, Guid PrincipalId);
public sealed record CredentialOAuthDescriptor(string ClientId, string RedirectUri, bool Synthetic, bool Configured, bool HostEnabled = true);
public sealed record CredentialOAuthAttempt(Guid AttemptId, Guid TenantId, Guid CredentialId, CredentialPurpose Purpose,
    Guid AccountId, Guid AuthenticationSessionId, Guid PrincipalId, long CredentialRevision, long AuthEpoch,
    string StateHash, string CorrelationHash, string ProtectedProof, CredentialOAuthDescriptor Provider,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, long Revision = 1, CredentialOAuthOutcome Outcome = CredentialOAuthOutcome.Pending,
    DateTimeOffset? ClaimedAt = null, DateTimeOffset? CompletedAt = null);
public sealed record CredentialOAuthPrepared(string AuthorizationUrl, string ProtectedProof);
public sealed record CredentialOAuthClaim(CredentialOAuthAttempt Attempt, string ProtectedProof);
// Provider tokens/DTOs stay in Infrastructure. Only verified normalized facts and a protected grant cross this port.
public sealed record CredentialOAuthGrant(string Subject, string EmailAddress, IReadOnlyList<string> Scopes, string ProtectedPayload, bool Synthetic);

public interface ITenantCredentialOAuthProvider
{
    CredentialOAuthDescriptor Describe(CredentialPurpose purpose);
    CredentialOAuthPrepared Prepare(CredentialOAuthAttempt attempt, string state);
    ValueTask<CredentialOAuthGrant> ExchangeAsync(CredentialOAuthClaim claim, string code, Credential current, CancellationToken ct);
    ValueTask<bool> RevokeAsync(Credential current, CancellationToken ct);
}
public interface ITenantCredentialOAuthStore
{
    ValueTask<CredentialOAuthAttempt?> ReadCredentialOAuthAttemptAsync(Guid attemptId, CancellationToken ct = default);
    ValueTask<bool> TryBeginCredentialOAuthAsync(TenantSecurityState next, long expectedSecurityRevision,
        CredentialOAuthAttempt attempt, AuthorizationAudit audit, CancellationToken ct = default);
    ValueTask<CredentialOAuthClaim?> TryClaimCredentialOAuthAsync(Guid attemptId, string stateHash, string correlationHash,
        long expectedSecurityRevision, CancellationToken ct = default);
    ValueTask<bool> TryCommitCredentialOAuthAsync(TenantSecurityState next, long expectedSecurityRevision, Guid attemptId, long claimRevision,
        Credential value, long expectedSecretRevision, AuthorizationAudit audit, CancellationToken ct = default);
    ValueTask<bool> TryDisconnectCredentialOAuthAsync(TenantSecurityState next, long expectedSecurityRevision, Guid credentialId,
        CredentialOAuthInitiator initiator, long expectedCredentialRevision, long expectedSecretRevision,
        AuthorizationAudit audit, CancellationToken ct = default);
    ValueTask FinishCredentialOAuthAsync(Guid attemptId, long claimRevision, CredentialOAuthOutcome outcome, CancellationToken ct = default);
    ValueTask<int> ExpireCredentialOAuthAttemptsAsync(int limit, CancellationToken ct = default);
}
