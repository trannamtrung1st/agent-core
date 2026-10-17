using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    public async ValueTask<SandboxVerificationAttempt?> FindSandboxVerificationAsync(Guid tenant, string profileId, Guid? attemptId = null, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.Set<TenantSandboxVerificationRecord>().AsNoTracking().Where(a => a.TenantId == tenant && a.ProfileId == profileId && (attemptId == null || a.AttemptId == attemptId))
            .OrderByDescending(a => a.SecurityRevision).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return row?.Domain();
    }
    public async ValueTask<SandboxVerificationEvidence> ReadSandboxVerificationAsync(Guid tenant, string profileId, long revision, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        if (!await OwnedSandboxProfiles(db, tenant).AnyAsync(p => p.Id == profileId && p.Revision == revision, ct).ConfigureAwait(false)) return new(null, false);
        var pending = await db.Set<TenantSandboxVerificationRecord>().AnyAsync(a => a.TenantId == tenant && a.ProfileId == profileId && a.Active, ct).ConfigureAwait(false);
        var row = await db.Set<SandboxVerificationRow>().AsNoTracking().SingleOrDefaultAsync(v => v.TenantId == tenant && v.ProfileId == profileId && v.ProfileRevision == revision, ct).ConfigureAwait(false);
        var value = row is null ? null : JsonSerializer.Deserialize<SandboxProfileVerification>(row.Json, SandboxJson);
        if (value is not null && (value.TenantId != tenant || value.ProfileId != profileId || value.ProfileRevision != revision))
            throw AgentCore.Application.Sessions.AgentCoreErrors.Persistence("Verification ownership is invalid.");
        return new(value, pending);
    }
    public ValueTask<bool> TryBeginSandboxVerificationAsync(TenantSecurityState next, long expectedSecurityRevision,
        SandboxVerificationAttempt attempt, SandboxProfile profile, HumanSessionReference human, AuthorizationAudit audit, CancellationToken ct = default)
    {
        TenantSandboxVerificationRules.ValidateStart(attempt, profile, human, expectedSecurityRevision);
        if (next.Tenant.TenantId != attempt.TenantId || next.Tenant.Revision != attempt.SecurityRevision || audit.ActorPrincipalId != human.PrincipalId)
            throw new ArgumentException("Invalid verification reservation.");
        return WriteTenantResourceAsync(next, expectedSecurityRevision, audit, async db =>
        {
            var row = await OwnedSandboxProfiles(db, attempt.TenantId).Where(p => p.Id == profile.Id).OrderByDescending(p => p.Revision).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            TenantSandboxVerificationRules.RequireCurrent(profile, row is null ? null : SandboxValue(row));
            if (await db.Set<TenantSandboxVerificationRecord>().AnyAsync(a => a.TenantId == attempt.TenantId && a.ProfileId == profile.Id && a.Active, ct).ConfigureAwait(false))
                throw new SecurityConflictException("verification_in_progress");
            if (profile.RegistryCredentialId is { } credential)
            {
                var value = await OwnedCredentials(db, attempt.TenantId).SingleOrDefaultAsync(c => c.CredentialId == credential.ToString("D"), ct).ConfigureAwait(false);
                TenantSandboxProfileRules.RequireCredential(profile, value is null ? null : SqliteCredentialStore.Map(value), (clock ?? TimeProvider.System).GetUtcNow());
            }
            await RequireAgentHumanAsync(db, attempt.TenantId, audit.ActorPrincipalId, human, ct).ConfigureAwait(false);
            db.Add(TenantSandboxVerificationRecord.Row(attempt)); return true;
        }, ct);
    }
    public async ValueTask<SandboxVerificationAttempt?> FinishSandboxVerificationAsync(Guid tenantId, Guid attemptId, long expectedRevision,
        SandboxVerificationOutcome outcome, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var row = await db.Set<TenantSandboxVerificationRecord>().SingleOrDefaultAsync(a => a.TenantId == tenantId && a.AttemptId == attemptId, ct).ConfigureAwait(false);
        if (row?.Revision != expectedRevision) return null; var current = row.Domain();
        if (current.Status != SandboxVerificationStatus.Running) return null;
        if (audit.TenantId != tenantId || audit.ActorPrincipalId != current.PrincipalId) throw new ArgumentException("Invalid verification settlement.");
        var tenant = await db.Set<Tenant>().SingleAsync(t => t.TenantId == tenantId, ct).ConfigureAwait(false);
        var latest = await OwnedSandboxProfiles(db, tenantId).Where(p => p.Id == current.ProfileId).OrderByDescending(p => p.Revision).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var allowed = current.LeaseExpiresAt > at && tenant.Revision == current.SecurityRevision && latest?.Revision == current.ProfileRevision;
        try { await RequireAgentHumanAsync(db, tenantId, current.PrincipalId, TenantSandboxVerificationRules.Human(current), ct).ConfigureAwait(false); }
        catch (AccessDeniedException) { allowed = false; }
        if (allowed && current.RegistryCredentialId is { } credential)
        {
            var value = await OwnedCredentials(db, tenantId).SingleOrDefaultAsync(c => c.CredentialId == credential.ToString("D"), ct).ConfigureAwait(false);
            var profile = SandboxValue(latest!) with { RegistryCredentialRevision = current.RegistryCredentialRevision, RegistrySecretRevision = current.RegistrySecretRevision };
            try { TenantSandboxProfileRules.RequireCredential(profile, value is null ? null : SqliteCredentialStore.Map(value), at); }
            catch (SecurityConflictException) { allowed = false; }
        }
        var settled = TenantSandboxVerificationRules.Complete(current, outcome, allowed, at);
        db.Entry(row).CurrentValues.SetValues(TenantSandboxVerificationRecord.Row(settled));
        if (TenantSandboxVerificationRules.AcceptObservation(settled))
        {
            var observation = await db.Set<SandboxVerificationRow>().SingleOrDefaultAsync(v => v.ProfileId == current.ProfileId && v.ProfileRevision == current.ProfileRevision, ct).ConfigureAwait(false);
            if (observation is null) { observation = new() { ProfileId = current.ProfileId, ProfileRevision = current.ProfileRevision }; db.Add(observation); }
            observation.TenantId = tenantId; observation.AttemptId = attemptId; observation.Json = JsonSerializer.Serialize(settled.Result, SandboxJson);
        }
        db.Entry(tenant).CurrentValues.SetValues(tenant with { Revision = checked(tenant.Revision + 1) });
        db.Add(audit with { Decision = settled.Status.ToString(), ReasonCode = "sandbox_verification_settled" });
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return settled; }
        catch (DbUpdateConcurrencyException) { return null; }
    }
    public async ValueTask<IReadOnlyList<SandboxVerificationCleanup>> ClaimSandboxVerificationCleanupAsync(DateTimeOffset at, Guid claimId, int limit = 10, CancellationToken ct = default)
    {
        if (claimId == Guid.Empty || limit is < 1 or > 100) throw new ArgumentException("Invalid cleanup claim.");
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false); var stamp = at.ToUnixTimeMilliseconds();
        var rows = await db.Set<TenantSandboxVerificationRecord>().Where(a => a.Active && a.LeaseExpiresAt <= stamp && (a.CleanupClaimExpiresAt == null || a.CleanupClaimExpiresAt <= stamp))
            .OrderBy(a => a.LeaseExpiresAt).Take(limit).ToArrayAsync(ct).ConfigureAwait(false);
        var result = new List<SandboxVerificationCleanup>();
        foreach (var row in rows)
        {
            var a = row.Domain(); var next = a with { Revision = checked(a.Revision + 1), Status = SandboxVerificationStatus.CleanupPending, CleanupClaim = claimId, CleanupClaimExpiresAt = at.AddSeconds(45) };
            db.Entry(row).CurrentValues.SetValues(TenantSandboxVerificationRecord.Row(next)); result.Add(new(a.TenantId, a.ProfileId, a.AttemptId, next.Revision, a.HostScope, claimId));
        }
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return result; }
        catch (DbUpdateConcurrencyException) { return []; }
    }
    public async ValueTask<bool> FinishSandboxVerificationCleanupAsync(SandboxVerificationCleanup claim, bool confirmed, DateTimeOffset at, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.Set<TenantSandboxVerificationRecord>().SingleOrDefaultAsync(a => a.TenantId == claim.TenantId && a.AttemptId == claim.AttemptId && a.ProfileId == claim.ProfileId, ct).ConfigureAwait(false);
        if (row is null) return false; var a = row.Domain();
        if (a.HostScope != claim.HostScope || a.Revision != claim.Revision || a.Status != SandboxVerificationStatus.CleanupPending || a.CleanupClaim != claim.ClaimId || a.CleanupClaimExpiresAt is not { } expires || expires <= at) return false;
        var next = a with { Revision = checked(a.Revision + 1), Status = confirmed ? SandboxVerificationStatus.Retired : SandboxVerificationStatus.CleanupPending,
            CleanupClaim = null, CleanupClaimExpiresAt = null, LeaseExpiresAt = at.AddSeconds(15) };
        db.Entry(row).CurrentValues.SetValues(TenantSandboxVerificationRecord.Row(next));
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); return true; } catch (DbUpdateConcurrencyException) { return false; }
    }
}
