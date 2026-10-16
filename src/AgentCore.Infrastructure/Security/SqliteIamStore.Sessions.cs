using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    private static IQueryable<TenantSessionRecord> OwnedSessions(AgentCoreDbContext db, Guid tenant) => db.Set<TenantSessionRecord>().Where(s => s.TenantId == tenant
        && db.Sessions.Any(row => row.SessionId == s.SessionId && row.DurablyDeletedAtUtc == null));
    public async ValueTask<TenantSession?> FindTenantSessionAsync(Guid tenantId, Guid sessionId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var root = await OwnedSessions(db, tenantId).AsNoTracking().SingleOrDefaultAsync(s => s.SessionId == sessionId.ToString("D"), ct).ConfigureAwait(false);
        return root is null ? null : await SessionView(db, root, ct).ConfigureAwait(false);
    }
    public async ValueTask<TenantSessionPage> ListTenantSessionsAsync(Guid tenantId, IReadOnlyList<Guid>? allowedIds, int page, int pageSize, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty || page < 0 || page > 100_000 || pageSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(page));
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = OwnedSessions(db, tenantId).AsNoTracking();
        if (allowedIds is not null) { var ids = allowedIds.Select(id => id.ToString("D")).ToArray(); query = query.Where(s => ids.Contains(s.SessionId)); }
        var total = await query.CountAsync(ct).ConfigureAwait(false);
        var roots = await query.OrderBy(s => s.SessionId).Skip(page * pageSize).Take(pageSize).ToArrayAsync(ct).ConfigureAwait(false);
        var items = new List<TenantSession>(); foreach (var root in roots) items.Add(await SessionView(db, root, ct).ConfigureAwait(false));
        return new(items, total, page, pageSize);
    }
    public ValueTask<bool> TryCreateTenantSessionAsync(TenantSecurityState next, long expectedSecurityRevision, TenantSessionRoot owner,
        SessionSnapshot snapshot, TenantSessionInitiator initiator, long expectedInstanceRevision, AuthorizationAudit audit, CancellationToken ct = default) =>
        WriteTenantResourceAsync(next, expectedSecurityRevision, audit, async db =>
        {
            if (owner.TenantId != next.Tenant.TenantId) throw new ArgumentException("Invalid tenant Session ownership.");
            await RequireHumanSessionAsync(db, owner.TenantId, initiator, ct).ConfigureAwait(false);
            var agent = await OwnedAgents(db, owner.TenantId).SingleOrDefaultAsync(a => a.Instance.InstanceId == owner.AgentInstanceId.ToString("D"), ct).ConfigureAwait(false);
            if (agent is null || agent.Instance.Revision != expectedInstanceRevision) return false;
            TenantSessionRules.RequireExecutor((await ReadTenantAsync(db, owner.TenantId, ct).ConfigureAwait(false))!, Owned(agent));
            TenantSessionRules.ValidateCreation(owner, snapshot, Owned(agent), initiator, audit);
            if (await db.Sessions.AnyAsync(s => s.SessionId == snapshot.SessionId.ToString("D"), ct).ConfigureAwait(false)
                || await db.Set<TenantSessionRecord>().AnyAsync(s => s.SessionId == snapshot.SessionId.ToString("D"), ct).ConfigureAwait(false)) return false;
            db.Sessions.Add(SqliteMemoryStore.ToRecord(snapshot));
            db.Add(TenantSessionRecord.Row(owner)); return true;
        }, ct);
    private static async Task<TenantSession> SessionView(AgentCoreDbContext db, TenantSessionRecord root, CancellationToken ct)
    {
        var row = await db.Sessions.AsNoTracking().Include(s => s.Snapshot).SingleAsync(s => s.SessionId == root.SessionId, ct).ConfigureAwait(false);
        return new(root.Domain(), SqliteMemoryStore.ToSnapshot(row, []));
    }
}
