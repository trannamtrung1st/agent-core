using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Experience;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;
public sealed partial class SqliteIamStore : ITenantExperienceManagementStore
{
    private async Task<TenantHumanExperienceOwner> HumanExperience(AgentCoreDbContext db, Guid tenant, Guid agent, HumanSessionReference human, CancellationToken ct)
    {
        await RequireProfileHumanAsync(db, tenant, human, ct).ConfigureAwait(false);
        var state = await ReadTenantAsync(db, tenant, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("experience_unavailable");
        var row = await OwnedAgents(db, tenant).SingleOrDefaultAsync(a => a.Instance.InstanceId == agent.ToString("D"), ct).ConfigureAwait(false) ?? throw new AccessDeniedException("experience_unavailable");
        return await TenantExperienceManagementRules.OwnerAsync(state, Owned(row), human, new IamAuthorizer(this), ct).ConfigureAwait(false);
    }
    private static IQueryable<TenantExperienceRecord> HumanExperienceRows(AgentCoreDbContext db, TenantHumanExperienceOwner owner)
    {
        var keys = owner.Keys;
        return db.Set<TenantExperienceRecord>().Where(i => i.TenantId == owner.TenantId && i.AgentInstanceId == owner.AgentId.ToString("D") && i.ExecutorPrincipalId == owner.ExecutorPrincipalId && keys.Contains(i.OwnerKey)
            && (i.UserPrincipalId == null && i.AccountId == null && i.UserId == null || i.UserPrincipalId == owner.User.PrincipalId && i.AccountId == owner.User.AccountId && i.UserId == owner.User.UserId));
    }
    public async ValueTask<TenantExperiencePage> ReadOwnTenantExperienceAsync(Guid tenant, Guid agent, HumanSessionReference human, TenantExperienceQuery query, CancellationToken ct = default)
    {
        TenantExperienceManagementRules.Validate(query);
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var owner = await HumanExperience(db, tenant, agent, human, ct).ConfigureAwait(false); var owned = HumanExperienceRows(db, owner).AsNoTracking();
        var retained = await owned.CountAsync(i => i.Visibility != (int)ExperienceVisibility.Deleted, ct).ConfigureAwait(false);
        var rows = owned.Where(i => (query.ExperienceId == null || i.ExperienceId == query.ExperienceId) && (query.IncludeHistorical || i.Visibility == (int)ExperienceVisibility.Eligible || i.Visibility == (int)ExperienceVisibility.Suppressed));
        var total = await rows.CountAsync(ct).ConfigureAwait(false);
        var page = await rows.OrderByDescending(i => i.CreatedAtUtc).ThenBy(i => i.ExperienceId).Skip(checked((query.Page - 1) * query.PageSize)).Take(query.PageSize).ToArrayAsync(ct).ConfigureAwait(false);
        return new(page.Select(i => new TenantExperienceItemView(i.Domain(), owner.CanManage && i.Visibility != (int)ExperienceVisibility.Deleted)).ToArray(), total, retained, owner.SecurityRevision, query.Page, query.PageSize, owner.AgentName, owner.CanManage && retained > 0);
    }
    public async ValueTask<TenantExperienceChangeResult?> TryChangeOwnTenantExperienceAsync(Guid tenant, Guid agent, HumanSessionReference human, TenantExperienceChange change, AuthorizationAudit audit, CancellationToken ct = default)
    {
        TenantExperienceManagementRules.Validate(change, tenant, agent, human, audit);
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var owner = await HumanExperience(db, tenant, agent, human, ct).ConfigureAwait(false); var before = await db.Set<Tenant>().SingleAsync(t => t.TenantId == tenant, ct).ConfigureAwait(false);
        if (before.Revision != change.ExpectedSecurityRevision || await db.Set<AuthorizationAudit>().AnyAsync(i => i.AuditId == audit.AuditId, ct).ConfigureAwait(false)) return null;
        if (!owner.CanManage) throw new AccessDeniedException("experience_manage_required");
        var rows = HumanExperienceRows(db, owner).Where(i => i.Visibility != (int)ExperienceVisibility.Deleted && (change.ExperienceId == null || i.ExperienceId == change.ExperienceId));
        var count = await rows.CountAsync(ct).ConfigureAwait(false);
        if (change.ExperienceId is not null && (count != 1 || !await rows.AnyAsync(i => i.Revision == change.ExpectedExperienceRevision, ct).ConfigureAwait(false)) || change.ExperienceId is null && count != change.ExpectedRetainedCount) return null;
        if (change.Visibility != ExperienceVisibility.Deleted && await rows.AnyAsync(i => i.Visibility == (int)ExperienceVisibility.Superseded, ct).ConfigureAwait(false)) throw new AccessDeniedException("experience_terminal");
        var at = audit.At.ToUnixTimeMilliseconds();
        try
        {
            var changed = change.Visibility == ExperienceVisibility.Deleted
                ? await rows.ExecuteUpdateAsync(set => set.SetProperty(i => i.Visibility, (int)change.Visibility).SetProperty(i => i.ContentJson, (string?)null).SetProperty(i => i.Revision, i => i.Revision + 1).SetProperty(i => i.UpdatedAtUtc, i => i.UpdatedAtUtc > at ? i.UpdatedAtUtc : at), ct).ConfigureAwait(false)
                : await rows.ExecuteUpdateAsync(set => set.SetProperty(i => i.Visibility, (int)change.Visibility).SetProperty(i => i.Revision, i => i.Revision + 1).SetProperty(i => i.UpdatedAtUtc, i => i.UpdatedAtUtc > at ? i.UpdatedAtUtc : at), ct).ConfigureAwait(false);
            if (changed != count) throw new SecurityConflictException("experience_changed");
            var revision = checked(before.Revision + 1); db.Entry(before).CurrentValues.SetValues(before with { Revision = revision }); db.Add(audit);
            await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return new(changed, revision);
        }
        catch (DbUpdateConcurrencyException) { return null; }
        catch (DbUpdateException ex) when (IsConstraint(ex)) { return null; }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6 or 19) { return null; }
    }
}
