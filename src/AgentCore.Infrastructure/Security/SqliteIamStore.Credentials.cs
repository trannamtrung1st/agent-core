using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    public async ValueTask<TenantCredential?> FindTenantCredentialAsync(Guid tenantId, Guid credentialId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await OwnedCredentials(db, tenantId).Include(c => c.EmailAccount).Include(c => c.DriveAccount).AsNoTracking()
            .SingleOrDefaultAsync(c => c.CredentialId == credentialId.ToString("D"), ct).ConfigureAwait(false);
        return row is null ? null : await CredentialView(db, tenantId, row, ct).ConfigureAwait(false);
    }
    public async ValueTask<TenantCredentialPage> ListTenantCredentialsAsync(Guid tenantId, IReadOnlyList<Guid>? allowedIds, int page, int pageSize, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty || page < 0 || page > 100_000 || pageSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(page));
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = OwnedCredentials(db, tenantId).AsNoTracking();
        if (allowedIds is not null) { var ids = allowedIds.Select(id => id.ToString("D")).ToArray(); query = query.Where(c => ids.Contains(c.CredentialId)); }
        var count = await query.CountAsync(ct).ConfigureAwait(false);
        var rows = await query.Include(c => c.EmailAccount).Include(c => c.DriveAccount).OrderBy(c => c.CredentialId)
            .Skip(page * pageSize).Take(pageSize).ToArrayAsync(ct).ConfigureAwait(false);
        var items = new List<TenantCredential>();
        foreach (var row in rows) if (await CredentialView(db, tenantId, row, ct).ConfigureAwait(false) is { } item) items.Add(item);
        return new(items, count, page, pageSize);
    }
    public ValueTask<bool> TrySaveTenantCredentialAsync(TenantSecurityState next, long expectedSecurityRevision, TenantCredentialRoot owner,
        Credential value, long expectedRevision, long expectedSecretRevision, AuthorizationAudit audit, CancellationToken ct = default)
    {
        next = IamStateRules.Freeze(next); value = TenantCredentialRules.Freeze(value);
        return WriteTenantResourceAsync(next, expectedSecurityRevision, audit, async db =>
        {
            if (owner.TenantId != next.Tenant.TenantId) throw new ArgumentException("Invalid credential ownership.");
            var key = owner.CredentialId.ToString("D");
            var root = await db.Set<TenantCredentialRecord>().SingleOrDefaultAsync(r => r.CredentialId == key, ct).ConfigureAwait(false);
            var row = await db.Credentials.Include(c => c.EmailAccount).Include(c => c.DriveAccount).SingleOrDefaultAsync(c => c.CredentialId == key, ct).ConfigureAwait(false);
            if (root is null ? expectedRevision != 0 || row is not null : root.Domain() != owner || root.DeletedAt is not null || row is null) return false;
            if ((row?.Revision ?? 0) != expectedRevision || (row?.SecretRevision ?? 0) != expectedSecretRevision) return false;
            value = TenantCredentialRules.Freeze(value);
            TenantCredentialRules.ValidateChange(owner, value, row is null ? null : SqliteCredentialStore.Map(row), expectedRevision, expectedSecretRevision);
            if (root is null) db.Add(TenantCredentialRecord.Row(owner));
            SqliteCredentialStore.StageValue(db, row, value, owner.TenantId); return true;
        }, ct);
    }
    public ValueTask<bool> TryDeleteTenantCredentialAsync(TenantSecurityState next, long expectedSecurityRevision, Guid credentialId,
        long expectedRevision, long expectedSecretRevision, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default)
    {
        next = IamStateRules.Freeze(next);
        return WriteTenantResourceAsync(next, expectedSecurityRevision, audit, async db =>
        {
            var key = credentialId.ToString("D");
            if (next.Assignments.Any(a => a.Scope == TenantCredentialRules.Scope(next.Tenant.TenantId, credentialId)))
                throw new ArgumentException("Deleting a credential must retire its direct assignments.");
            var root = await db.Set<TenantCredentialRecord>().SingleOrDefaultAsync(r => r.CredentialId == key && r.TenantId == next.Tenant.TenantId && r.DeletedAt == null, ct).ConfigureAwait(false);
            var row = await db.Credentials.SingleOrDefaultAsync(c => c.CredentialId == key, ct).ConfigureAwait(false);
            if (root is null || row is null || row.Revision != expectedRevision || row.SecretRevision != expectedSecretRevision) return false;
            if (await db.AgentCredentialBindings.AnyAsync(b => b.CredentialId == key, ct).ConfigureAwait(false)) throw new SecurityConflictException("credential_in_use");
            // Mark the real owner tombstone before the incumbent Credential FK
            // cascades its watch. The outer security/audit transaction keeps this
            // invisible until the protected value and watch retire together.
            db.Entry(root).CurrentValues.SetValues(root with { DeletedAt = at });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            db.Credentials.Remove(row); return true;
        }, ct);
    }
    private async ValueTask<bool> WriteTenantResourceAsync(TenantSecurityState next, long expected, AuthorizationAudit audit,
        Func<AgentCoreDbContext, Task<bool>> change, CancellationToken ct)
    {
        next = IamStateRules.Freeze(next);
        if (audit.TenantId != next.Tenant.TenantId || next.Tenant.Revision != checked(expected + 1)) throw new ArgumentException("Invalid credential transaction.");
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var current = await db.Set<Tenant>().SingleOrDefaultAsync(t => t.TenantId == next.Tenant.TenantId, ct).ConfigureAwait(false);
        if (current is null || current.Revision != expected) return false;
        var before = (await ReadTenantAsync(db, current.TenantId, ct).ConfigureAwait(false))!;
        IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next);
        await ValidateTenantActorAsync(db, before, audit.ActorPrincipalId, ct).ConfigureAwait(false);
        await EnsureActiveAccountOwnerAsync(db, next, null, ct).ConfigureAwait(false);
        await EnsureResourceOwnershipAsync(db, next, ct).ConfigureAwait(false);
        try
        {
            if (!await change(db).ConfigureAwait(false)) return false;
            db.Entry(current).CurrentValues.SetValues(next.Tenant); await ApplyTenantChildrenAsync(db, next, ct).ConfigureAwait(false); db.Add(audit);
            await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return true;
        }
        catch (DbUpdateConcurrencyException) { return false; }
        catch (DbUpdateException ex) when (IsConstraint(ex)) { return false; }
    }
    private static IQueryable<CredentialRecord> OwnedCredentials(AgentCoreDbContext db, Guid tenant) => db.Credentials.Where(c =>
        db.Set<TenantCredentialRecord>().Any(r => r.CredentialId == c.CredentialId && r.TenantId == tenant && r.DeletedAt == null)
        && (c.EmailAccount == null || c.EmailAccount.TenantId == tenant) && (c.DriveAccount == null || c.DriveAccount.TenantId == tenant));
    private static async Task<TenantCredential?> CredentialView(AgentCoreDbContext db, Guid tenant, CredentialRecord row, CancellationToken ct)
    {
        var root = await db.Set<TenantCredentialRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.CredentialId == row.CredentialId && r.TenantId == tenant && r.DeletedAt == null, ct).ConfigureAwait(false);
        return root is null ? null : new(root.Domain(), TenantCredentialRules.Freeze(SqliteCredentialStore.Map(row)),
            await db.AgentCredentialBindings.CountAsync(b => b.CredentialId == row.CredentialId, ct).ConfigureAwait(false));
    }
}
