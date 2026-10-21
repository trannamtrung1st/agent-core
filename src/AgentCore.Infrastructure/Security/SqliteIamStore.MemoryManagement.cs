using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Memory;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore : ITenantMemoryManagementStore
{
    private async Task<TenantHumanMemoryOwner> HumanMemory(AgentCoreDbContext db, Guid tenant, Guid agentId, HumanSessionReference human, TenantMemoryQuery query, CancellationToken ct)
    {
        await RequireProfileHumanAsync(db, tenant, human, ct).ConfigureAwait(false);
        var state = await ReadTenantAsync(db, tenant, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("memory_unavailable");
        var row = await OwnedAgents(db, tenant).SingleOrDefaultAsync(a => a.Instance.InstanceId == agentId.ToString("D"), ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("memory_unavailable");
        var session = query.SessionId is { } id ? await ReadSandboxSession(db, tenant, id, ct).ConfigureAwait(false) : null;
        return await TenantMemoryManagementRules.OwnerAsync(state, Owned(row), human, query, new IamAuthorizer(this), session, ct).ConfigureAwait(false);
    }
    private static IQueryable<TenantLearnedMemoryRecord> HumanMemoryRows(AgentCoreDbContext db, TenantHumanMemoryOwner owner)
    {
        var agents = owner.ReadableAgents.Select(i => i.ToString("D")).ToArray(); var scope = (int)owner.Query.Scope;
        var keys = TenantMemoryManagementRules.OwnerKeys(owner).ToArray();
        var query = db.Set<TenantLearnedMemoryRecord>().Where(i => i.TenantId == owner.TenantId && i.Scope == scope && keys.Contains(i.OwnerKey) && agents.Contains(i.AgentInstanceId)
            && (i.UserPrincipalId == owner.User.PrincipalId && i.AccountId == owner.User.AccountId && i.UserId == owner.User.UserId
                || i.UserPrincipalId == null && scope == (int)MemoryScope.Session && i.AccountId == null && i.UserId == null));
        if (scope == (int)MemoryScope.Session) query = query.Where(i => i.SessionId == owner.Query.SessionId!.Value.ToString("D") && i.AgentInstanceId == owner.AgentId.ToString("D"));
        if (owner.Query.MemoryId is { } memory) query = query.Where(i => i.MemoryId == memory);
        return query;
    }
    public async ValueTask<TenantMemoryPage> ReadOwnTenantMemoryAsync(Guid tenant, Guid agent, HumanSessionReference human, TenantMemoryQuery query, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var owner = await HumanMemory(db, tenant, agent, human, query, ct).ConfigureAwait(false); var rows = HumanMemoryRows(db, owner).AsNoTracking();
        var total = await rows.CountAsync(ct).ConfigureAwait(false); var retained = rows.Where(i => i.Status != (int)MemoryItemStatus.Deleted);
        var count = await retained.CountAsync(ct).ConfigureAwait(false); var writable = owner.WritableAgents.Select(i => i.ToString("D")).ToArray();
        var canReset = count > 0 && !await retained.AnyAsync(i => !writable.Contains(i.AgentInstanceId), ct).ConfigureAwait(false);
        var page = await rows.OrderByDescending(i => i.CreatedAtUtc).ThenBy(i => i.MemoryId).Skip(checked((query.Page - 1) * query.PageSize)).Take(query.PageSize).ToArrayAsync(ct).ConfigureAwait(false);
        var revision = await db.Set<Tenant>().Where(t => t.TenantId == tenant).Select(t => t.Revision).SingleAsync(ct).ConfigureAwait(false);
        return new(page.Select(i => new TenantMemoryItemView(i.Domain(), i.Status != (int)MemoryItemStatus.Deleted && writable.Contains(i.AgentInstanceId))).ToArray(), total, count, query.Page, query.PageSize, revision, canReset, owner.AgentName);
    }
    public async ValueTask<TenantMemoryForgetResult?> TryForgetOwnTenantMemoryAsync(Guid tenant, Guid agent, HumanSessionReference human,
        TenantMemoryForget change, AuthorizationAudit audit, CancellationToken ct = default)
    {
        TenantMemoryManagementRules.Validate(change, tenant, agent, human, audit);
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var owner = await HumanMemory(db, tenant, agent, human, change.Query, ct).ConfigureAwait(false);
        var before = await db.Set<Tenant>().SingleAsync(t => t.TenantId == tenant, ct).ConfigureAwait(false);
        if (before.Revision != change.ExpectedSecurityRevision || await db.Set<AuthorizationAudit>().AnyAsync(a => a.AuditId == audit.AuditId, ct).ConfigureAwait(false)) return null;
        var rows = HumanMemoryRows(db, owner).Where(i => i.Status != (int)MemoryItemStatus.Deleted);
        if (change.MemoryId is { } id) rows = rows.Where(i => i.MemoryId == id && i.Revision == change.ExpectedMemoryRevision);
        var count = await rows.CountAsync(ct).ConfigureAwait(false);
        if (change.MemoryId is not null && count != 1 || change.MemoryId is null && count != change.ExpectedRetainedCount) return null;
        var writable = owner.WritableAgents.Select(i => i.ToString("D")).ToArray();
        if (await rows.AnyAsync(i => !writable.Contains(i.AgentInstanceId), ct).ConfigureAwait(false)) throw new AccessDeniedException("memory_manage_required");
        var at = audit.At.ToUnixTimeMilliseconds();
        try
        {
            var changed = await rows.ExecuteUpdateAsync(set => set.SetProperty(i => i.Status, (int)MemoryItemStatus.Deleted)
                .SetProperty(i => i.Subject, "").SetProperty(i => i.SubjectKey, "").SetProperty(i => i.Content, "")
                .SetProperty(i => i.Revision, i => i.Revision + 1).SetProperty(i => i.UpdatedAtUtc, i => i.UpdatedAtUtc > at ? i.UpdatedAtUtc : at), ct).ConfigureAwait(false);
            if (changed != count) throw new SecurityConflictException("memory_changed");
            var nextRevision = checked(before.Revision + 1);
            db.Entry(before).CurrentValues.SetValues(before with { Revision = nextRevision }); db.Add(audit);
            await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false);
            return new(changed, nextRevision);
        }
        catch (DbUpdateConcurrencyException) { return null; }
        catch (DbUpdateException error) when (IsConstraint(error)) { return null; }
        catch (SqliteException error) when (error.SqliteErrorCode is 5 or 6) { return null; }
    }
}
