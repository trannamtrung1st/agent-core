using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    public async ValueTask<TenantRunPage> ListTenantSessionRunsAsync(Guid tenant, Guid? session, IReadOnlyList<Guid>? allowed, int page, int pageSize, CancellationToken ct = default, Guid? instanceId = null, IReadOnlyList<Guid>? allowedSessions = null)
    {
        if (page < 0 || page > 100_000 || pageSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(page));
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        if (session is null && instanceId is null) throw new ArgumentException("An exact Session or Agent is required.");
        if (session is { } id && !await OwnedSessions(db, tenant).AnyAsync(s => s.SessionId == id.ToString("D"), ct).ConfigureAwait(false)) throw new AccessDeniedException("resource_unavailable");
        var query = OwnedRuns(db, tenant).AsNoTracking().Where(r => OwnedSessions(db, tenant).Any(s => s.SessionId == r.SessionId));
        if (session is { } sessionId) query = query.Where(r => r.SessionId == sessionId.ToString("D"));
        if (instanceId is { } instance) query = query.Where(r => r.AgentInstanceId == instance.ToString("D"));
        if (allowedSessions is not null) { var ids = allowedSessions.Select(id => id.ToString("D")).ToArray(); query = query.Where(r => ids.Contains(r.SessionId)); }
        if (allowed is not null) { var ids = allowed.Select(id => id.ToString("D")).ToArray(); query = query.Where(r => ids.Contains(r.AgentRunId)); }
        var total = await query.CountAsync(ct).ConfigureAwait(false);
        var rows = await query.OrderByDescending(r => r.CreatedAtUtc).ThenBy(r => r.AgentRunId).Skip(page * pageSize).Take(pageSize).ToArrayAsync(ct).ConfigureAwait(false);
        return new(rows.Select(AgentRunStoreMapping.ToDomain).ToArray(), total, page, pageSize);
    }
    public async ValueTask<CompletionInboxItem?> FindTenantCompletionAsync(AgentRunOwner owner, Guid childRunId, CancellationToken ct = default)
    {
        TenantRuntimeRules.RequireOwner(owner); await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        if (await CompletionSource(db, owner, childRunId, ct).ConfigureAwait(false) is null) return null;
        var row = await db.BackgroundCompletionReceipts.AsNoTracking().SingleOrDefaultAsync(r => r.ChildAgentRunId == childRunId.ToString("D") && r.ProfileId == null, ct).ConfigureAwait(false);
        var item = row is null ? null : CompletionInboxMapping.Read(row);
        return item?.Owner == owner ? item : null;
    }
}
