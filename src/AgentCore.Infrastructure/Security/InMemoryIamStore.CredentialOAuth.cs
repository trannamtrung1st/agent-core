using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    public ValueTask<bool> TryCheckCredentialOAuthAsync(TenantSecurityState next, long expectedSecurityRevision, Guid credentialId,
        CredentialOAuthInitiator initiator, long expectedCredentialRevision, long expectedSecretRevision, CredentialOAuthCheck check,
        AuthorizationAudit audit, CancellationToken ct = default) => ValueTask.FromResult(WriteCredential(next, expectedSecurityRevision, audit, () =>
        {
            if (audit.ActorPrincipalId != initiator.PrincipalId) throw new ArgumentException("Invalid OAuth actor.");
            RequireHumanSession(next.Tenant.TenantId, initiator);
            if (!CredentialBacking().ApplyTenantOAuthCheck(next.Tenant.TenantId, credentialId, expectedCredentialRevision, expectedSecretRevision, check, OAuthNow)) return false;
            if (check.ReconnectRequired) SupersedeOAuth(next.Tenant.TenantId, credentialId);
            return true;
        }, ct));
    private readonly Dictionary<Guid, CredentialOAuthAttempt> _credentialOAuthAttempts = [];
    private DateTimeOffset OAuthNow => (clock ?? TimeProvider.System).GetUtcNow();
    public ValueTask<CredentialOAuthAttempt?> ReadCredentialOAuthAttemptAsync(Guid id, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(_credentialOAuthAttempts.GetValueOrDefault(id)); }
    public ValueTask<bool> TryBeginCredentialOAuthAsync(TenantSecurityState next, long expectedSecurityRevision, CredentialOAuthAttempt attempt,
        AuthorizationAudit audit, CancellationToken ct = default)
    {
        next = IamStateRules.Freeze(next); attempt = TenantCredentialOAuthRules.Attempt(attempt);
        return ValueTask.FromResult(WriteCredential(next, expectedSecurityRevision, audit, () =>
        {
            if (attempt.TenantId != next.Tenant.TenantId || audit.ActorPrincipalId != attempt.PrincipalId) throw new ArgumentException("Invalid OAuth ownership.");
            RequireHumanSession(attempt.TenantId, new(attempt.AccountId, attempt.AuthenticationSessionId, attempt.PrincipalId));
            var current = CredentialBacking().ReadTenantCredential(attempt.TenantId, attempt.CredentialId)?.Value;
            if (current is null || current.Revision != attempt.CredentialRevision || current.Purpose != attempt.Purpose || current.Status != CredentialStatus.Active
                || !TenantCredentialOAuthRules.IsOAuth(current) || TenantCredentialOAuthRules.Epoch(current) != attempt.AuthEpoch || attempt.CreatedAt > OAuthNow || attempt.ExpiresAt <= OAuthNow
                || _credentialOAuthAttempts.ContainsKey(attempt.AttemptId)) return false;
            if (_credentialOAuthAttempts.Values.Count(a => a.TenantId == attempt.TenantId && a.CredentialId != attempt.CredentialId && a.ExpiresAt > OAuthNow
                && a.Outcome is CredentialOAuthOutcome.Pending or CredentialOAuthOutcome.Claimed) >= 128) throw new SecurityConflictException("oauth_attempt_limit");
            SupersedeOAuth(attempt.TenantId, attempt.CredentialId);
            _credentialOAuthAttempts.Add(attempt.AttemptId, attempt); return true;
        }, ct));
    }
    public ValueTask<CredentialOAuthClaim?> TryClaimCredentialOAuthAsync(Guid id, string stateHash, string correlationHash, long expectedSecurityRevision, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (instances.CredentialGate)
        {
            var a = _credentialOAuthAttempts.GetValueOrDefault(id); var now = OAuthNow;
            if (a is null || a.Outcome != CredentialOAuthOutcome.Pending || a.ExpiresAt <= now || _tenants.GetValueOrDefault(a.TenantId)?.Tenant.Revision != expectedSecurityRevision
                || !TenantCredentialOAuthRules.DigestMatches(a.StateHash, stateHash) || !TenantCredentialOAuthRules.DigestMatches(a.CorrelationHash, correlationHash)) return ValueTask.FromResult<CredentialOAuthClaim?>(null);
            RequireHumanSession(a.TenantId, new(a.AccountId, a.AuthenticationSessionId, a.PrincipalId));
            var current = CredentialBacking().ReadTenantCredential(a.TenantId, a.CredentialId)?.Value;
            if (current is null || current.Revision != a.CredentialRevision || current.Purpose != a.Purpose || current.Status != CredentialStatus.Active
                || !TenantCredentialOAuthRules.IsOAuth(current) || TenantCredentialOAuthRules.Epoch(current) != a.AuthEpoch) return ValueTask.FromResult<CredentialOAuthClaim?>(null);
            var claimed = a with { ProtectedProof = "", ClaimedAt = now, Outcome = CredentialOAuthOutcome.Claimed, Revision = checked(a.Revision + 1) };
            _credentialOAuthAttempts[id] = claimed; return ValueTask.FromResult<CredentialOAuthClaim?>(new(claimed, a.ProtectedProof));
        }
    }
    public ValueTask<bool> TryCommitCredentialOAuthAsync(TenantSecurityState next, long expectedSecurityRevision, Guid id, long claimRevision, Credential value,
        long expectedSecretRevision, AuthorizationAudit audit, CancellationToken ct = default)
    {
        next = IamStateRules.Freeze(next); value = TenantCredentialRules.Freeze(value);
        return ValueTask.FromResult(WriteCredential(next, expectedSecurityRevision, audit, () =>
        {
            var a = _credentialOAuthAttempts.GetValueOrDefault(id); var now = OAuthNow;
            if (a is null || a.Outcome != CredentialOAuthOutcome.Claimed || a.Revision != claimRevision || a.ExpiresAt <= now
                || a.TenantId != next.Tenant.TenantId || audit.ActorPrincipalId != a.PrincipalId || value.CredentialId != a.CredentialId) return false;
            RequireHumanSession(a.TenantId, new(a.AccountId, a.AuthenticationSessionId, a.PrincipalId));
            var current = CredentialBacking().ReadTenantCredential(a.TenantId, a.CredentialId)?.Value;
            if (current is null || current.Revision != a.CredentialRevision || !TenantCredentialOAuthRules.IsOAuth(current) || TenantCredentialOAuthRules.Epoch(current) != a.AuthEpoch) return false;
            TenantCredentialOAuthValidation.RequireConnection(current, value, a.Provider.Synthetic);
            if (!CredentialBacking().SaveTenantOAuthAuthorization(a.TenantId, value, a.CredentialRevision, expectedSecretRevision)) return false;
            _credentialOAuthAttempts[id] = a with { CompletedAt = now, Outcome = CredentialOAuthOutcome.Connected, Revision = checked(a.Revision + 1) }; return true;
        }, ct));
    }
    public ValueTask<bool> TryDisconnectCredentialOAuthAsync(TenantSecurityState next, long expectedSecurityRevision, Guid credentialId, CredentialOAuthInitiator initiator,
        long expectedCredentialRevision, long expectedSecretRevision, AuthorizationAudit audit, CancellationToken ct = default)
    {
        next = IamStateRules.Freeze(next);
        return ValueTask.FromResult(WriteCredential(next, expectedSecurityRevision, audit, () =>
        {
            if (audit.ActorPrincipalId != initiator.PrincipalId) throw new ArgumentException("Invalid OAuth actor.");
            RequireHumanSession(next.Tenant.TenantId, initiator);
            var current = CredentialBacking().ReadTenantCredential(next.Tenant.TenantId, credentialId)?.Value;
            if (current is null || current.Revision != expectedCredentialRevision || current.SecretRevision != expectedSecretRevision) return false;
            if (!CredentialBacking().SaveTenantOAuthAuthorization(next.Tenant.TenantId, TenantCredentialOAuthRules.Disconnect(current, OAuthNow), expectedCredentialRevision, expectedSecretRevision)) return false;
            SupersedeOAuth(next.Tenant.TenantId, credentialId); return true;
        }, ct));
    }
    public ValueTask FinishCredentialOAuthAsync(Guid id, long revision, CredentialOAuthOutcome outcome, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); if (outcome is not (CredentialOAuthOutcome.Denied or CredentialOAuthOutcome.Failed)) throw new ArgumentException("Invalid OAuth terminal outcome.");
        lock (_gate) { if (_credentialOAuthAttempts.GetValueOrDefault(id) is { Outcome: CredentialOAuthOutcome.Claimed } a && a.Revision == revision)
            _credentialOAuthAttempts[id] = a with { Outcome = outcome, CompletedAt = OAuthNow, ProtectedProof = "", Revision = checked(a.Revision + 1) }; }
        return ValueTask.CompletedTask;
    }
    public ValueTask<int> ExpireCredentialOAuthAttemptsAsync(int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        lock (_gate)
        {
            var now = OAuthNow; var expired = _credentialOAuthAttempts.Values.Where(a => a.ExpiresAt <= now && a.Outcome is CredentialOAuthOutcome.Pending or CredentialOAuthOutcome.Claimed).OrderBy(a => a.ExpiresAt).Take(limit).ToArray();
            foreach (var a in expired) _credentialOAuthAttempts[a.AttemptId] = a with { Outcome = CredentialOAuthOutcome.Expired, CompletedAt = now, ProtectedProof = "", Revision = checked(a.Revision + 1) };
            return ValueTask.FromResult(expired.Length);
        }
    }
    private void SupersedeOAuth(Guid tenant, Guid credential)
    {
        var now = OAuthNow;
        foreach (var a in _credentialOAuthAttempts.Values.Where(a => a.TenantId == tenant && a.CredentialId == credential && a.Outcome is CredentialOAuthOutcome.Pending or CredentialOAuthOutcome.Claimed).ToArray())
            _credentialOAuthAttempts[a.AttemptId] = a with { Outcome = CredentialOAuthOutcome.Superseded, CompletedAt = now, ProtectedProof = "", Revision = checked(a.Revision + 1) };
    }
    private void RequireHumanSession(Guid tenant, HumanSessionReference initiator)
    {
        var state = _tenants.GetValueOrDefault(tenant); var account = _accounts.GetValueOrDefault(initiator.AccountId); var session = _sessions.GetValueOrDefault(initiator.AuthenticationSessionId);
        if (state?.Tenant.Status != IdentityStatus.Active || account?.Status != IdentityStatus.Active || session is null || session.AccountId != account.AccountId
            || session.RevokedAt is not null || session.ExpiresAt <= OAuthNow || session.SecurityStamp != account.SecurityStamp
            || !state.Principals.Any(p => p.PrincipalId == initiator.PrincipalId && p.Kind == PrincipalKind.User && p.AccountId == account.AccountId && p.Status == IdentityStatus.Active)
            || !state.Users.Any(u => u.PrincipalId == initiator.PrincipalId && u.AccountId == account.AccountId && u.Status == IdentityStatus.Active)) throw new AccessDeniedException("oauth_initiator_unavailable");
    }
}
