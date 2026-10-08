using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class SqliteAgentRunStore
{
    public async ValueTask<CompletionInboxItem?> GetCompletionInboxAsync(AgentRunOwner owner, Guid parentSessionId, Guid childRunId, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.BackgroundCompletionReceipts.SingleOrDefaultAsync(i => i.ChildAgentRunId == childRunId.ToString("D")
            && i.AgentInstanceId == owner.AgentInstanceId.ToString("D") && i.ProfileId == owner.ProfileId.ToString("D") && i.ParentSessionId == parentSessionId.ToString("D"), ct).ConfigureAwait(false);
        if (row is null) return null;
        var item = CompletionInboxMapping.Read(row);
        return item;
    }

    public async ValueTask<bool> HasCompletionClaimAsync(AgentRunOwner owner, Guid parentRunId, Guid generation, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.BackgroundCompletionReceipts.AsNoTracking().Where(i => i.AgentInstanceId == owner.AgentInstanceId.ToString("D") && i.ProfileId == owner.ProfileId.ToString("D")
            && i.ClaimRunId == parentRunId.ToString("D") && i.Status == (int)CompletionInboxStatus.Claimed && i.ClaimExpiresAtUtc > now.ToUnixTimeMilliseconds()).ToArrayAsync(ct).ConfigureAwait(false);
        return rows.Select(CompletionInboxMapping.Read).Any(i => i.ClaimGeneration == generation);
    }

    public async ValueTask<IReadOnlyList<AgentCore.Application.Ports.BackgroundCompletionCandidate>> ListBackgroundPageAsync(AgentRunOwner owner, Guid parentSessionId, Guid? cursor, bool pendingOnly, int limit, CancellationToken ct = default)
    {
        if (limit is < 1 or > AgentRunLimits.MaxListLimit) throw AgentCoreErrors.Validation("Background limit must be 1–100.");
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var parentKey = "\"originatingSessionId\":\"" + parentSessionId.ToString("D") + "\"";
        var initialKey = "\"initialBackgroundAgentRunId\":\"";
        var query = db.AgentRuns.AsNoTracking().Where(r => r.AgentInstanceId == owner.AgentInstanceId.ToString("D") && r.ProfileId == owner.ProfileId.ToString("D")
            && db.Sessions.Any(s => s.SessionId == r.SessionId && s.OriginJson.Contains(parentKey) && s.OriginJson.Contains(initialKey + r.AgentRunId + "\"")));
        if (cursor is Guid id)
        {
            var anchor = await query.SingleOrDefaultAsync(r => r.AgentRunId == id.ToString("D"), ct).ConfigureAwait(false) ?? throw AgentCoreErrors.NotFound("Background cursor was not found.");
            query = query.Where(r => r.CreatedAtUtc < anchor.CreatedAtUtc || r.CreatedAtUtc == anchor.CreatedAtUtc && string.Compare(r.AgentRunId, anchor.AgentRunId) < 0);
        }
        if (pendingOnly) query = query.Where(r => db.BackgroundCompletionReceipts.Any(i => i.ChildAgentRunId == r.AgentRunId && i.Status == (int)CompletionInboxStatus.Pending));
        var rows = await query.OrderByDescending(r => r.CreatedAtUtc).ThenByDescending(r => r.AgentRunId).Take(limit).ToArrayAsync(ct).ConfigureAwait(false);
        var result = new List<AgentCore.Application.Ports.BackgroundCompletionCandidate>();
        foreach (var row in rows)
        {
            var run = AgentRunStoreMapping.ToDomain(row);
            var child = await sessions.LoadMetadataAsync(run.SessionId, ct).ConfigureAwait(false);
            if (child is not null) result.Add(new(run, child));
        }
        return result;
    }

    public async ValueTask<bool> HasCompletionAcknowledgmentAsync(AgentRunOwner owner, Guid parentRunId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.BackgroundCompletionReceipts.AsNoTracking().Where(i => i.AgentInstanceId == owner.AgentInstanceId.ToString("D") && i.ProfileId == owner.ProfileId.ToString("D")
            && i.ClaimRunId == parentRunId.ToString("D")).ToArrayAsync(ct).ConfigureAwait(false);
        return rows.Select(CompletionInboxMapping.Read).Any(i => i.Status == CompletionInboxStatus.Claimed && i.Acknowledgment is not null);
    }

    public async ValueTask<IReadOnlyList<AgentCore.Application.Ports.BackgroundCompletionCandidate>> ListBackgroundChildrenAsync(AgentRunOwner owner, Guid parentSessionId, int limit, CancellationToken ct = default)
    {
        if (limit is < 1 or > AgentRunLimits.MaxListLimit) throw AgentCoreErrors.Validation("Background limit must be 1–100.");
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var parentKey = "\"originatingSessionId\":\"" + parentSessionId.ToString("D") + "\"";
        var initialKey = "\"initialBackgroundAgentRunId\":\"";
        var rows = await db.AgentRuns.AsNoTracking().Where(r => r.AgentInstanceId == owner.AgentInstanceId.ToString("D") && r.ProfileId == owner.ProfileId.ToString("D")
            && db.Sessions.Any(s => s.SessionId == r.SessionId && s.OriginJson.Contains(parentKey) && s.OriginJson.Contains(initialKey + r.AgentRunId + "\"")))
            .OrderByDescending(r => r.CreatedAtUtc).ThenByDescending(r => r.AgentRunId).Take(limit).ToArrayAsync(ct).ConfigureAwait(false);
        var result = new List<AgentCore.Application.Ports.BackgroundCompletionCandidate>();
        foreach (var row in rows)
        {
            var run = AgentRunStoreMapping.ToDomain(row);
            var child = await sessions.LoadMetadataAsync(run.SessionId, ct).ConfigureAwait(false);
            if (child is not null) result.Add(new(run, child));
        }
        return result;
    }

    private async Task<IReadOnlyList<AgentCore.Application.Ports.BackgroundCompletionCandidate>> WaitChildrenAsync(AgentCoreDbContext db, IReadOnlyList<Guid> sessionIds, CancellationToken ct)
    {
        var result = new List<AgentCore.Application.Ports.BackgroundCompletionCandidate>();
        var keys = sessionIds.Select(id => id.ToString("D")).ToArray();
        foreach (var row in await db.AgentRuns.AsNoTracking().Where(r => keys.Contains(r.SessionId)).ToArrayAsync(ct).ConfigureAwait(false))
        {
            var run = AgentRunStoreMapping.ToDomain(row);
            var child = await sessions.LoadMetadataAsync(run.SessionId, ct).ConfigureAwait(false);
            if (child is not null) result.Add(new(run, child));
        }
        return result;
    }

    private async Task RepairInboxAsync(AgentCoreDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var missing = await db.AgentRuns.AsNoTracking().Where(r => (r.Status == (int)AgentRunStatus.Completed || r.Status == (int)AgentRunStatus.Failed || r.Status == (int)AgentRunStatus.Cancelled)
            && !db.BackgroundCompletionReceipts.Any(i => i.ChildAgentRunId == r.AgentRunId)
            && db.Sessions.Any(s => s.SessionId == r.SessionId && s.OriginJson.Contains("\"reportCompletionToOrigin\":true")
                && s.OriginJson.Contains("\"initialBackgroundAgentRunId\":\"" + r.AgentRunId + "\"")))
            .OrderBy(r => r.UpdatedAtUtc).ThenBy(r => r.AgentRunId).Take(AgentRunLimits.MaxListLimit).ToArrayAsync(ct).ConfigureAwait(false);
        foreach (var row in missing)
        {
            var run = AgentRunStoreMapping.ToDomain(row);
            var child = await sessions.LoadMetadataAsync(run.SessionId, ct).ConfigureAwait(false);
            if (child is not null && child.Origin.MayReportCompletion(run.AgentRunId)) db.BackgroundCompletionReceipts.Add(CompletionInboxMapping.Create(run, child));
        }
        var claimed = await db.BackgroundCompletionReceipts.Where(i => i.Status == (int)CompletionInboxStatus.Claimed).OrderBy(i => i.ClaimExpiresAtUtc).ThenBy(i => i.ChildAgentRunId).Take(AgentRunLimits.MaxListLimit).ToArrayAsync(ct).ConfigureAwait(false);
        var parentIds = claimed.Select(i => i.ClaimRunId).Distinct().ToArray();
        var parents = (await db.AgentRuns.AsNoTracking().Where(r => parentIds.Contains(r.AgentRunId)).ToArrayAsync(ct).ConfigureAwait(false)).Select(AgentRunStoreMapping.ToDomain).ToArray();
        foreach (var row in claimed) CompletionInboxMapping.Write(row, CompletionInboxMapping.Refresh(CompletionInboxMapping.Read(row), parents, now));
    }

    private async Task SettleInboxAsync(AgentCoreDbContext db, AgentRun updated, Guid? generation, CancellationToken ct)
    {
        foreach (var row in await db.BackgroundCompletionReceipts.Where(i => i.ClaimRunId == updated.AgentRunId.ToString("D") || i.ParentActivationId == updated.ActivationId.ToString("D")).ToArrayAsync(ct).ConfigureAwait(false))
            CompletionInboxMapping.Write(row, CompletionInboxMapping.Read(row).Settle(updated, generation));
        if (updated.IsTerminal && !db.BackgroundCompletionReceipts.Local.Any(i => i.ChildAgentRunId == updated.AgentRunId.ToString("D"))
            && !await db.BackgroundCompletionReceipts.AnyAsync(i => i.ChildAgentRunId == updated.AgentRunId.ToString("D"), ct).ConfigureAwait(false))
        {
            var child = await sessions.LoadMetadataAsync(updated.SessionId, ct).ConfigureAwait(false);
            if (child is not null && child.Origin.MayReportCompletion(updated.AgentRunId))
                db.BackgroundCompletionReceipts.Add(CompletionInboxMapping.Create(updated, child));
        }
    }

    public async ValueTask<IReadOnlyList<CompletionInboxItem>> ListCompletionInboxAsync(AgentRunOwner owner, Guid parentSessionId, int limit, DateTimeOffset now, CancellationToken ct = default)
    {
        if (limit is < 1 or > AgentRunLimits.MaxListLimit) throw AgentCoreErrors.Validation("Inbox limit must be 1–100.");
        var parent = await sessions.LoadMetadataAsync(parentSessionId, ct).ConfigureAwait(false);
        if (parent?.AgentInstanceId != owner.AgentInstanceId || parent.ProfileId != owner.ProfileId) throw AgentCoreErrors.NotFound("Parent Session was not found.");
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.BackgroundCompletionReceipts.AsNoTracking().Where(r => r.AgentInstanceId == owner.AgentInstanceId.ToString("D") && r.ProfileId == owner.ProfileId.ToString("D") && r.ParentSessionId == parentSessionId.ToString("D"))
            .OrderBy(r => r.CreatedAtUtc).ThenBy(r => r.ChildAgentRunId).Take(limit).ToArrayAsync(ct).ConfigureAwait(false);
        return rows.Select(CompletionInboxMapping.Read).Where(i => i.ParentSessionId == parentSessionId)
            .OrderBy(i => i.SourceFinishedAtUtc).ThenBy(i => i.ChildAgentRunId).Take(limit).ToArray();
    }

    public ValueTask<CompletionInboxItem> TakeCompletionAsync(AgentRunOwner owner, Guid parentRunId, Guid generation, Guid childRunId, long expectedRevision, string toolCallId, Guid token, DateTimeOffset now, CancellationToken ct = default) =>
        MutateInboxAsync(owner, parentRunId, generation, childRunId, now, (i, p) => i.Take(p, expectedRevision, toolCallId, token, now), ct);
    public ValueTask<CompletionInboxItem> AcknowledgeCompletionAsync(AgentRunOwner owner, Guid parentRunId, Guid generation, Guid childRunId, long expectedRevision, Guid token, string usage, DateTimeOffset now, CancellationToken ct = default) =>
        MutateInboxAsync(owner, parentRunId, generation, childRunId, now, (i, p) => i.Acknowledge(p, expectedRevision, token, usage, now), ct);

    private async ValueTask<CompletionInboxItem> MutateInboxAsync(AgentRunOwner owner, Guid parentRunId, Guid generation, Guid childRunId, DateTimeOffset now, Func<CompletionInboxItem, AgentRun, CompletionInboxItem> apply, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var parentRow = await db.AgentRuns.AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == parentRunId.ToString("D"), ct).ConfigureAwait(false);
        var parent = parentRow is null ? null : AgentRunStoreMapping.ToDomain(parentRow);
        CompletionInboxMapping.RequireParent(parent, owner, generation, now);
        await RepairInboxAsync(db, now, ct).ConfigureAwait(false);
        var row = db.BackgroundCompletionReceipts.Local.SingleOrDefault(i => i.ChildAgentRunId == childRunId.ToString("D"))
            ?? await db.BackgroundCompletionReceipts.SingleOrDefaultAsync(i => i.ChildAgentRunId == childRunId.ToString("D"), ct).ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Completion was not found.");
        var item = CompletionInboxMapping.Read(row);
        if (item.Owner != owner || item.ParentSessionId != parent!.SessionId) throw AgentCoreErrors.NotFound("Completion was not found.");
        try { item = apply(item, parent); }
        catch (Exception e) when (e is AgentRunTransitionException or ArgumentException) { throw AgentRunStoreMapping.Map(e); }
        CompletionInboxMapping.Write(row, item);
        await SaveAsync(db, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return item;
    }
}
