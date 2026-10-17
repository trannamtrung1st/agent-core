using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore : ITenantConversationStore
{
    public async ValueTask<TenantConversationPage?> ReadTenantConversationAsync(Guid tenant, Guid session, long? after, long? before,
        int limit, CancellationToken ct = default)
    {
        TenantConversationRules.RequirePaging(after, before, limit);
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var root = await OwnedSessions(db, tenant).AsNoTracking().SingleOrDefaultAsync(s => s.SessionId == session.ToString("D"), ct).ConfigureAwait(false);
        if (root is null) return null;
        var revision = await db.Sessions.Where(s => s.SessionId == root.SessionId).Select(s => s.Revision).SingleAsync(ct).ConfigureAwait(false);
        var entries = db.Entries.AsNoTracking().Where(e => e.SessionId == root.SessionId);
        var rows = after is { } cursor
            ? await entries.Where(e => e.EntrySequence > cursor).OrderBy(e => e.EntrySequence).Take(limit).ToArrayAsync(ct).ConfigureAwait(false)
            : await entries.Where(e => before == null || e.EntrySequence < before).OrderByDescending(e => e.EntrySequence).Take(limit + 1).ToArrayAsync(ct).ConfigureAwait(false);
        var history = after is { } next
            ? HistoryPaging.FromForward(rows.Select(SqliteMemoryStore.ToEntry).ToArray(), next, limit)
            : HistoryPaging.FromNewestFirst(rows.Select(SqliteMemoryStore.ToEntry).ToArray(), limit);
        return new(root.Domain(), revision, history);
    }
}
