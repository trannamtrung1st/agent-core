using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    private DateTimeOffset OAuthNow => (clock ?? TimeProvider.System).GetUtcNow();

    public async ValueTask<CredentialOAuthAttempt?> ReadCredentialOAuthAttemptAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        return (await db.Set<CredentialOAuthAttemptRow>().AsNoTracking().SingleOrDefaultAsync(a => a.AttemptId == id, ct).ConfigureAwait(false))?.Domain();
    }

    public ValueTask<bool> TryBeginCredentialOAuthAsync(TenantSecurityState next, long expectedSecurityRevision, CredentialOAuthAttempt attempt,
        AuthorizationAudit audit, CancellationToken ct = default)
    {
        next = IamStateRules.Freeze(next); attempt = TenantCredentialOAuthRules.Attempt(attempt);
        return WriteCredentialAsync(next, expectedSecurityRevision, audit, async db =>
        {
            if (attempt.TenantId != next.Tenant.TenantId || audit.ActorPrincipalId != attempt.PrincipalId) throw new ArgumentException("Invalid OAuth ownership.");
            await RequireOAuthInitiatorAsync(db, attempt.TenantId, new(attempt.AccountId, attempt.AuthenticationSessionId, attempt.PrincipalId), ct).ConfigureAwait(false);
            var row = await OAuthCredentialAsync(db, attempt.TenantId, attempt.CredentialId, ct).ConfigureAwait(false);
            var current = row is null ? null : SqliteCredentialStore.Map(row); var now = OAuthNow;
            if (current is null || current.Revision != attempt.CredentialRevision || current.Purpose != attempt.Purpose || current.Status != CredentialStatus.Active
                || !TenantCredentialOAuthRules.IsOAuth(current) || TenantCredentialOAuthRules.Epoch(current) != attempt.AuthEpoch
                || attempt.CreatedAt > now || attempt.ExpiresAt <= now || await db.Set<CredentialOAuthAttemptRow>().AnyAsync(a => a.AttemptId == attempt.AttemptId, ct).ConfigureAwait(false)) return false;
            if (await ActiveOAuth(db).CountAsync(a => a.TenantId == attempt.TenantId && a.CredentialId != row!.CredentialId && a.ExpiresAt > now, ct).ConfigureAwait(false) >= 128)
                throw new SecurityConflictException("oauth_attempt_limit");
            // Execute before inserting the successor, so the partial unique index is never transiently violated.
            await SupersedeOAuthAsync(db, attempt.TenantId, attempt.CredentialId, now, ct).ConfigureAwait(false);
            db.Add(CredentialOAuthAttemptRow.Map(attempt)); return true;
        }, ct);
    }

    public async ValueTask<CredentialOAuthClaim?> TryClaimCredentialOAuthAsync(Guid id, string stateHash, string correlationHash,
        long expectedSecurityRevision, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var row = await db.Set<CredentialOAuthAttemptRow>().SingleOrDefaultAsync(a => a.AttemptId == id, ct).ConfigureAwait(false); var now = OAuthNow;
        if (row is null || row.Outcome != CredentialOAuthOutcome.Pending || row.ExpiresAt <= now
            || !TenantCredentialOAuthRules.DigestMatches(row.StateHash, stateHash) || !TenantCredentialOAuthRules.DigestMatches(row.CorrelationHash, correlationHash)
            || !await db.Set<Tenant>().AnyAsync(t => t.TenantId == row.TenantId && t.Revision == expectedSecurityRevision, ct).ConfigureAwait(false)) return null;
        await RequireOAuthInitiatorAsync(db, row.TenantId, new(row.AccountId, row.AuthenticationSessionId, row.PrincipalId), ct).ConfigureAwait(false);
        var credential = await OAuthCredentialAsync(db, row.TenantId, Guid.Parse(row.CredentialId), ct).ConfigureAwait(false);
        var current = credential is null ? null : SqliteCredentialStore.Map(credential);
        if (current is null || current.Revision != row.CredentialRevision || current.Purpose.ToString() != row.Purpose || current.Status != CredentialStatus.Active
            || !TenantCredentialOAuthRules.IsOAuth(current) || TenantCredentialOAuthRules.Epoch(current) != row.AuthEpoch) return null;
        var proof = row.ProtectedProof;
        var claimed = row with { Outcome = CredentialOAuthOutcome.Claimed, ProtectedProof = "", ClaimedAt = now, Revision = checked(row.Revision + 1) };
        db.Entry(row).CurrentValues.SetValues(claimed);
        await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false);
        return new(claimed.Domain(), proof);
    }

    public ValueTask<bool> TryCommitCredentialOAuthAsync(TenantSecurityState next, long expectedSecurityRevision, Guid id, long claimRevision,
        Credential value, long expectedSecretRevision, AuthorizationAudit audit, CancellationToken ct = default)
    {
        next = IamStateRules.Freeze(next); value = TenantCredentialRules.Freeze(value);
        return WriteCredentialAsync(next, expectedSecurityRevision, audit, async db =>
        {
            var a = await db.Set<CredentialOAuthAttemptRow>().SingleOrDefaultAsync(a => a.AttemptId == id, ct).ConfigureAwait(false); var now = OAuthNow;
            if (a is null || a.Outcome != CredentialOAuthOutcome.Claimed || a.Revision != claimRevision || a.ExpiresAt <= now
                || a.TenantId != next.Tenant.TenantId || audit.ActorPrincipalId != a.PrincipalId || value.CredentialId.ToString("D") != a.CredentialId) return false;
            await RequireOAuthInitiatorAsync(db, a.TenantId, new(a.AccountId, a.AuthenticationSessionId, a.PrincipalId), ct).ConfigureAwait(false);
            var row = await OAuthCredentialAsync(db, a.TenantId, value.CredentialId, ct).ConfigureAwait(false);
            if (row is null || row.Revision != a.CredentialRevision || row.SecretRevision != expectedSecretRevision) return false;
            var current = SqliteCredentialStore.Map(row);
            if (!TenantCredentialOAuthRules.IsOAuth(current) || TenantCredentialOAuthRules.Epoch(current) != a.AuthEpoch) return false;
            TenantCredentialOAuthValidation.RequireConnection(current, value, a.Domain().Provider.Synthetic);
            await RequireUniqueOAuthSubjectAsync(db, a.TenantId, value, ct).ConfigureAwait(false);
            SqliteCredentialStore.StageValue(db, row, value, a.TenantId);
            db.Entry(a).CurrentValues.SetValues(a with { Outcome = CredentialOAuthOutcome.Connected, CompletedAt = now, Revision = checked(a.Revision + 1) });
            return true;
        }, ct);
    }

    public ValueTask<bool> TryDisconnectCredentialOAuthAsync(TenantSecurityState next, long expectedSecurityRevision, Guid credentialId,
        CredentialOAuthInitiator initiator, long expectedCredentialRevision, long expectedSecretRevision, AuthorizationAudit audit, CancellationToken ct = default)
    {
        next = IamStateRules.Freeze(next);
        return WriteCredentialAsync(next, expectedSecurityRevision, audit, async db =>
        {
            if (audit.ActorPrincipalId != initiator.PrincipalId) throw new ArgumentException("Invalid OAuth actor.");
            await RequireOAuthInitiatorAsync(db, next.Tenant.TenantId, initiator, ct).ConfigureAwait(false);
            var row = await OAuthCredentialAsync(db, next.Tenant.TenantId, credentialId, ct).ConfigureAwait(false);
            if (row is null || row.Revision != expectedCredentialRevision || row.SecretRevision != expectedSecretRevision) return false;
            var value = TenantCredentialOAuthRules.Disconnect(SqliteCredentialStore.Map(row), OAuthNow);
            SqliteCredentialStore.StageValue(db, row, value, next.Tenant.TenantId);
            await SupersedeOAuthAsync(db, next.Tenant.TenantId, credentialId, OAuthNow, ct).ConfigureAwait(false); return true;
        }, ct);
    }

    public async ValueTask FinishCredentialOAuthAsync(Guid id, long revision, CredentialOAuthOutcome outcome, CancellationToken ct = default)
    {
        if (outcome is not (CredentialOAuthOutcome.Denied or CredentialOAuthOutcome.Failed)) throw new ArgumentException("Invalid OAuth terminal outcome.");
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); var now = OAuthNow;
        await db.Set<CredentialOAuthAttemptRow>().Where(a => a.AttemptId == id && a.Revision == revision && a.Outcome == CredentialOAuthOutcome.Claimed)
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.Outcome, outcome).SetProperty(a => a.ProtectedProof, "")
                .SetProperty(a => a.CompletedAt, now).SetProperty(a => a.Revision, a => a.Revision + 1), ct).ConfigureAwait(false);
    }

    public async ValueTask<int> ExpireCredentialOAuthAttemptsAsync(int limit, CancellationToken ct = default)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); var now = OAuthNow;
        var ids = ActiveOAuth(db).Where(a => a.ExpiresAt <= now).OrderBy(a => a.ExpiresAt).ThenBy(a => a.AttemptId).Take(limit).Select(a => a.AttemptId);
        return await ActiveOAuth(db).Where(a => ids.Contains(a.AttemptId))
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.Outcome, CredentialOAuthOutcome.Expired).SetProperty(a => a.ProtectedProof, "")
                .SetProperty(a => a.CompletedAt, now).SetProperty(a => a.Revision, a => a.Revision + 1), ct).ConfigureAwait(false);
    }

    private static IQueryable<CredentialOAuthAttemptRow> ActiveOAuth(AgentCoreDbContext db) => db.Set<CredentialOAuthAttemptRow>()
        .Where(a => a.Outcome == CredentialOAuthOutcome.Pending || a.Outcome == CredentialOAuthOutcome.Claimed);
    private static Task<int> SupersedeOAuthAsync(AgentCoreDbContext db, Guid tenant, Guid credential, DateTimeOffset now, CancellationToken ct) =>
        ActiveOAuth(db).Where(a => a.TenantId == tenant && a.CredentialId == credential.ToString("D"))
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.Outcome, CredentialOAuthOutcome.Superseded).SetProperty(a => a.ProtectedProof, "")
                .SetProperty(a => a.CompletedAt, now).SetProperty(a => a.Revision, a => a.Revision + 1), ct);
    private static Task<CredentialRecord?> OAuthCredentialAsync(AgentCoreDbContext db, Guid tenant, Guid id, CancellationToken ct) =>
        OwnedCredentials(db, tenant).Include(c => c.EmailAccount).Include(c => c.DriveAccount).SingleOrDefaultAsync(c => c.CredentialId == id.ToString("D"), ct);

    private async Task RequireOAuthInitiatorAsync(AgentCoreDbContext db, Guid tenant, CredentialOAuthInitiator initiator, CancellationToken ct)
    {
        var now = OAuthNow;
        var live = await (from session in db.Set<AuthenticationSession>()
            join account in db.Set<Account>() on session.AccountId equals account.AccountId
            join user in db.Set<TenantUser>() on account.AccountId equals user.AccountId
            join principal in db.Set<Principal>() on user.PrincipalId equals principal.PrincipalId
            join owner in db.Set<Tenant>() on user.TenantId equals owner.TenantId
            where session.SessionId == initiator.AuthenticationSessionId && account.AccountId == initiator.AccountId
                && account.Status == IdentityStatus.Active && session.RevokedAt == null && session.ExpiresAt > now && session.SecurityStamp == account.SecurityStamp
                && owner.TenantId == tenant && owner.Status == IdentityStatus.Active && user.Status == IdentityStatus.Active
                && principal.PrincipalId == initiator.PrincipalId && principal.TenantId == tenant && principal.AccountId == account.AccountId
                && principal.Kind == PrincipalKind.User && principal.Status == IdentityStatus.Active
            select session.SessionId).AnyAsync(ct).ConfigureAwait(false);
        if (!live) throw new AccessDeniedException("oauth_initiator_unavailable");
    }

    private static async Task RequireUniqueOAuthSubjectAsync(AgentCoreDbContext db, Guid tenant, Credential value, CancellationToken ct)
    {
        var id = value.CredentialId.ToString("D");
        var duplicate = value.EmailAccount?.GoogleSubject is { } email
            ? await db.Set<EmailAccountProfileRecord>().AnyAsync(p => p.TenantId == tenant && p.CredentialId != id && p.GoogleSubject == email, ct).ConfigureAwait(false)
            : value.DriveAccount?.GoogleSubject is { } drive
                && await db.Set<DriveAccountProfileRecord>().AnyAsync(p => p.TenantId == tenant && p.CredentialId != id && p.GoogleSubject == drive, ct).ConfigureAwait(false);
        if (duplicate) throw new SecurityConflictException("oauth_identity_already_connected");
    }
}
