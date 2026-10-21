using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore : ITenantUserProfileStore
{
    public async ValueTask<TenantUserProfileView> ReadOwnTenantUserProfileAsync(Guid tenant, HumanSessionReference human, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await RequireProfileHumanAsync(db, tenant, human, ct).ConfigureAwait(false);
        var user = await db.Set<TenantUser>().AsNoTracking().SingleAsync(u => u.TenantId == tenant && u.PrincipalId == human.PrincipalId && u.AccountId == human.AccountId, ct).ConfigureAwait(false);
        var profile = await db.Set<TenantUserProfileRecord>().AsNoTracking().SingleOrDefaultAsync(p => p.TenantId == tenant && p.PrincipalId == human.PrincipalId && p.AccountId == human.AccountId, ct).ConfigureAwait(false);
        var revision = await db.Set<Tenant>().Where(t => t.TenantId == tenant).Select(t => t.Revision).SingleAsync(ct).ConfigureAwait(false);
        return new(profile?.Domain() ?? TenantUserProfileRules.Empty(user).Freeze(), revision);
    }
    public async ValueTask<TenantUserProfileView?> TrySaveOwnTenantUserProfileAsync(TenantUserProfile profile, HumanSessionReference human,
        long expectedSecurityRevision, long expectedProfileRevision, AuthorizationAudit audit, CancellationToken ct = default)
    {
        profile = profile.Freeze(); TenantUserProfileRules.Commit(profile, human, expectedSecurityRevision, expectedProfileRevision, audit);
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await RequireProfileHumanAsync(db, profile.TenantId, human, ct).ConfigureAwait(false);
        var tenant = await db.Set<Tenant>().SingleAsync(t => t.TenantId == profile.TenantId, ct).ConfigureAwait(false);
        var old = await db.Set<TenantUserProfileRecord>().SingleOrDefaultAsync(p => p.TenantId == profile.TenantId && p.PrincipalId == profile.PrincipalId, ct).ConfigureAwait(false);
        var created = await db.Set<TenantUser>().Where(u => u.TenantId == profile.TenantId && u.PrincipalId == profile.PrincipalId).Select(u => u.CreatedAt).SingleAsync(ct).ConfigureAwait(false);
        if (tenant.Revision != expectedSecurityRevision || (old?.Revision ?? 0) != expectedProfileRevision || created.ToUnixTimeMilliseconds() > profile.UpdatedAt.ToUnixTimeMilliseconds()
            || old is not null && (old.AccountId != profile.AccountId || old.UpdatedAtUtc > profile.UpdatedAt.ToUnixTimeMilliseconds())
            || await db.Set<AuthorizationAudit>().AnyAsync(a => a.AuditId == audit.AuditId, ct).ConfigureAwait(false)) return null;
        var row = TenantUserProfileRecord.Row(profile);
        if (old is null) db.Add(row); else db.Entry(old).CurrentValues.SetValues(row);
        db.Entry(tenant).CurrentValues.SetValues(tenant with { Revision = checked(tenant.Revision + 1) }); db.Add(audit);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false);
            return new(profile, checked(expectedSecurityRevision + 1));
        }
        catch (DbUpdateConcurrencyException) { return null; }
        catch (DbUpdateException error) when (IsConstraint(error)) { return null; }
        catch (SqliteException error) when (error.SqliteErrorCode is 5 or 6) { return null; }
    }
    private static async Task RequireUserProfileReferencesAsync(AgentCore.Infrastructure.Persistence.AgentCoreDbContext db, TenantSecurityState next, CancellationToken ct)
    {
        var owners = await (from profile in db.Set<TenantUserProfileRecord>().AsNoTracking()
            join user in db.Set<TenantUser>().AsNoTracking() on new { profile.TenantId, profile.PrincipalId, profile.AccountId }
                equals new { user.TenantId, user.PrincipalId, user.AccountId }
            where profile.TenantId == next.Tenant.TenantId select new { user.UserId, user.PrincipalId, user.AccountId }).ToArrayAsync(ct).ConfigureAwait(false);
        if (owners.Any(owner => !next.Users.Any(u => u.UserId == owner.UserId && u.PrincipalId == owner.PrincipalId && u.AccountId == owner.AccountId)))
            throw new ArgumentException("Retain the actual User Profile membership owner; change its status without reparenting it.");
    }
    private async Task RequireProfileHumanAsync(AgentCore.Infrastructure.Persistence.AgentCoreDbContext db, Guid tenant, HumanSessionReference human, CancellationToken ct)
    {
        try { await RequireHumanSessionAsync(db, tenant, human, ct).ConfigureAwait(false); }
        catch (AccessDeniedException) { throw new AccessDeniedException("initiator_unavailable"); }
    }
}
