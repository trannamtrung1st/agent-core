using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class SqliteAgentRunStore
{
    public async ValueTask<IReadOnlyList<AgentCore.Application.Ports.BackgroundCompletionCandidate>> ListBackgroundChildrenAsync(AgentRunOwner owner, Guid parentSessionId, int limit, CancellationToken ct = default)
    {
        if (owner.TenantId is not null) throw new AgentCore.Application.Security.AccessDeniedException("tenant_context_required");
        if (limit is < 1 or > AgentRunLimits.MaxListLimit) throw AgentCoreErrors.Validation("Background limit must be 1–100.");
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var parentKey = "\"originatingSessionId\":\"" + parentSessionId.ToString("D") + "\"";
        var initialKey = "\"initialBackgroundAgentRunId\":\"";
        var rows = await LegacyRuns(db).AsNoTracking().Where(r => r.AgentInstanceId == owner.AgentInstanceId.ToString("D") && r.ProfileId == AgentRunStoreMapping.ProfileKey(owner)
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

    private async Task RepairInboxAsync(AgentCoreDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var missing = await LegacyRuns(db).AsNoTracking().Where(r => (r.Status == (int)AgentRunStatus.Completed || r.Status == (int)AgentRunStatus.Failed || r.Status == (int)AgentRunStatus.Cancelled)
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

    }

    private async Task SettleInboxAsync(AgentCoreDbContext db, AgentRun updated, Guid? generation, CancellationToken ct)
    {
        foreach (var row in await db.BackgroundCompletionReceipts.Where(i => i.ParentActivationId == updated.ActivationId.ToString("D")).ToArrayAsync(ct).ConfigureAwait(false))
            CompletionInboxMapping.Write(row, CompletionInboxMapping.Read(row).Settle(updated));
        if (updated.IsTerminal && !db.BackgroundCompletionReceipts.Local.Any(i => i.ChildAgentRunId == updated.AgentRunId.ToString("D"))
            && !await db.BackgroundCompletionReceipts.AnyAsync(i => i.ChildAgentRunId == updated.AgentRunId.ToString("D"), ct).ConfigureAwait(false))
        {
            var child = await sessions.LoadMetadataAsync(updated.SessionId, ct).ConfigureAwait(false);
            if (child is not null && child.Origin.MayReportCompletion(updated.AgentRunId))
                db.BackgroundCompletionReceipts.Add(CompletionInboxMapping.Create(updated, child));
        }
    }

}
