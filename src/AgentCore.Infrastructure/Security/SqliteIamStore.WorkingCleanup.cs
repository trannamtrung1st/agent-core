using AgentCore.Application.Ports;
using AgentCore.Application.Workspaces;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore : ITenantWorkingCleanupStore
{
    public async ValueTask<TenantWorkingCleanupPage> ListTenantWorkingCleanupAsync(Guid? after, int limit, CancellationToken ct = default)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); var cursor = after?.ToString("D");
        var rows = await db.Set<TenantSessionRecord>().AsNoTracking().Where(o => db.Sessions.Any(s => s.SessionId == o.SessionId
            && (s.DurablyDeletedAtUtc != null || s.Snapshot!.LifecycleStatus == "Completed" || s.Snapshot!.LifecycleStatus == "Expired" || s.Snapshot!.LifecycleStatus == "Cancelled" || s.Snapshot!.LifecycleStatus == "Ended"))
            && (cursor == null || string.Compare(o.SessionId, cursor) > 0)).OrderBy(o => o.SessionId).Take(limit + 1).ToArrayAsync(ct).ConfigureAwait(false);
        return new(rows.Take(limit).Select(o => o.Domain()).ToArray(), rows.Length > limit ? Guid.Parse(rows[limit - 1].SessionId) : null);
    }
    public async ValueTask<TenantWorkingCleanupTarget?> ReadTenantWorkingCleanupAsync(TenantSessionRoot owner, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var state = await SessionSettlement(db, owner, ct).ConfigureAwait(false);
        return state is null ? null : TenantWorkingCleanupRules.Target(state.Value.Graph, state.Value.Lease);
    }

    public async ValueTask<bool> RetireTenantRunContentAsync(TenantSessionRoot owner, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var id = owner.SessionId.ToString("D");
        var root = await db.Set<TenantSessionRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == owner.TenantId && r.SessionId == id, ct).ConfigureAwait(false);
        if (root?.Domain() != owner) return false;
        var session = await db.Sessions.AsNoTracking().SingleAsync(s => s.SessionId == id, ct).ConfigureAwait(false);
        if (session.DurablyDeletedAtUtc is not null)
            await RetireRunContent(db, owner.TenantId, id, (clock ?? TimeProvider.System).GetUtcNow(), ct).ConfigureAwait(false);
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return true; }
        catch (DbUpdateConcurrencyException) { return false; }
    }
}
