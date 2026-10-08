using AgentCore.Application.Ports;
using AgentCore.Application.Execution;
using AgentCore.Application.Observability;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class SqliteAgentRunStore
{
    public ValueTask<AgentRunAdmissionResult> AdmitCompletionReportAsync(SessionSnapshot parent, long expectedRevision,
        AgentRun report, Guid childRunId, CancellationToken ct = default) =>
        AdmitCoreAsync(parent, expectedRevision, report, null, ct, completionSourceRunId: childRunId);

    public async ValueTask<IReadOnlyList<BackgroundCompletionCandidate>> ListUnreportedCompletionsAsync(int limit, CancellationToken ct = default)
    {
        if (limit is < 1 or > AgentRunLimits.MaxListLimit) throw AgentCoreErrors.Validation("Completion query requires a bounded limit.");
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.AgentRuns.AsNoTracking().Where(row => (row.Status == (int)AgentRunStatus.Completed
                || row.Status == (int)AgentRunStatus.Cancelled || row.Status == (int)AgentRunStatus.Failed)
                && !db.BackgroundCompletionReceipts.Any(receipt => receipt.ChildAgentRunId == row.AgentRunId)
                && db.Sessions.Any(session => session.SessionId == row.SessionId && session.OriginJson.Contains("\"reportCompletionToOrigin\":true")
                    && session.OriginJson.Contains("\"initialBackgroundAgentRunId\":\"" + row.AgentRunId + "\"")))
            .OrderBy(row => row.UpdatedAtUtc).ThenBy(row => row.AgentRunId).Take(limit).ToArrayAsync(ct).ConfigureAwait(false);
        var result = new List<BackgroundCompletionCandidate>();
        foreach (var row in rows)
        {
            var run = AgentRunStoreMapping.ToDomain(row);
            var session = await sessions.LoadMetadataAsync(run.SessionId, ct).ConfigureAwait(false);
            if (session is not null && session.Origin.MayReportCompletion(run.AgentRunId)) result.Add(new(run, session));
        }
        return result;
    }

    public async ValueTask<CompletionDeliveryState> GetCompletionDeliveryAsync(AgentRunOwner owner, Guid childRunId, CancellationToken ct = default)
    {
        var child = await GetAsync(owner, childRunId, ct).ConfigureAwait(false) ?? throw AgentCoreErrors.NotFound("Child run was not found.");
        var session = await sessions.LoadMetadataAsync(child.SessionId, ct).ConfigureAwait(false) ?? throw AgentCoreErrors.NotFound("Child Session was not found.");
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var receipt = await db.BackgroundCompletionReceipts.AsNoTracking().SingleOrDefaultAsync(r => r.ChildAgentRunId == childRunId.ToString("D")
            && r.AgentInstanceId == owner.AgentInstanceId.ToString("D") && r.ProfileId == owner.ProfileId.ToString("D"), ct).ConfigureAwait(false);
        var reportRow = receipt?.ParentActivationId is { } id ? await db.AgentRuns.AsNoTracking().SingleOrDefaultAsync(r => r.ActivationId == id, ct).ConfigureAwait(false) : null;
        return CompletionDeliveryProjection.Build(session, reportRow is null ? null : AgentRunStoreMapping.ToDomain(reportRow), receipt is not null, receipt?.SkipReason);
    }

    public async ValueTask<bool> HasCompletionReceiptAsync(AgentRunOwner owner, Guid childRunId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.BackgroundCompletionReceipts.AnyAsync(receipt => receipt.ChildAgentRunId == childRunId.ToString("D")
            && receipt.AgentInstanceId == owner.AgentInstanceId.ToString("D") && receipt.ProfileId == owner.ProfileId.ToString("D"), ct).ConfigureAwait(false);
    }
    public async ValueTask SkipCompletionReportAsync(AgentRunOwner owner, Guid childRunId, string reason, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.AgentRuns.AsNoTracking().SingleOrDefaultAsync(row => row.AgentRunId == childRunId.ToString("D")
            && row.AgentInstanceId == owner.AgentInstanceId.ToString("D") && row.ProfileId == owner.ProfileId.ToString("D"), ct).ConfigureAwait(false);
        if (row is null || !AgentRunStoreMapping.ToDomain(row).IsTerminal) throw AgentCoreErrors.NotFound("Completed child run was not found.");
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"INSERT OR IGNORE INTO BackgroundCompletionReceipts (ChildAgentRunId, AgentInstanceId, ProfileId, ParentActivationId, SkipReason, CreatedAtUtc) VALUES ({childRunId.ToString("D")}, {owner.AgentInstanceId.ToString("D")}, {owner.ProfileId.ToString("D")}, NULL, {reason}, {now.ToUnixTimeMilliseconds()})", ct).ConfigureAwait(false);
        RuntimeTelemetry.RecordBackgroundSession(inserted == 1 ? "completion-skipped" : "completion-deduped");
    }
}
