using AgentCore.Application.Ports;
using AgentCore.Application.Execution;
using AgentCore.Application.Observability;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class SqliteAgentRunStore
{
    public ValueTask<AgentRunAdmissionResult> AdmitCompletionReportBatchAsync(SessionSnapshot parent, long expectedRevision,
        AgentRun report, IReadOnlyList<Guid> childRunIds, CancellationToken ct = default)
    {
        if (childRunIds.Count is < 1 or > 2 || childRunIds.Distinct().Count() != childRunIds.Count
            || childRunIds[0] != report.Admission.Activation.SourceAgentRunId) throw AgentCoreErrors.Validation("Completion batch requires one or two unique sources and its primary identity.");
        return AdmitCoreAsync(parent, expectedRevision, report, null, ct, completionSourceRunId: childRunIds[0], additionalCompletionSources: childRunIds.Skip(1).ToArray());
    }

    public ValueTask<AgentRunAdmissionResult> AdmitCompletionReportAsync(SessionSnapshot parent, long expectedRevision,
        AgentRun report, Guid childRunId, CancellationToken ct = default) =>
        AdmitCoreAsync(parent, expectedRevision, report, null, ct, completionSourceRunId: childRunId);

    public async ValueTask<IReadOnlyList<BackgroundCompletionCandidate>> ListDeliveryCandidatesAsync(int limit, DateTimeOffset now, CancellationToken ct = default)
    {
        if (limit is < 1 or > AgentRunLimits.MaxListLimit) throw AgentCoreErrors.Validation("Completion query requires a bounded limit.");
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await RepairInboxAsync(db, now, ct).ConfigureAwait(false);
        await SaveAsync(db, ct).ConfigureAwait(false);
        var items = await db.BackgroundCompletionReceipts.AsNoTracking().Where(i => i.Status == (int)CompletionInboxStatus.Pending
            && (db.AgentRuns.Any(c => c.AgentRunId == i.ChildAgentRunId && c.PayloadJson.Contains("\"outcomeKind\":1"))
                || !db.AgentRuns.Any(p => p.SessionId == i.ParentSessionId && p.Status != (int)AgentRunStatus.Completed && p.Status != (int)AgentRunStatus.Failed && p.Status != (int)AgentRunStatus.Cancelled)
                    && !db.Sessions.Any(p => p.SessionId == i.ParentSessionId && p.PendingAgentInputIdsJson != "[]")))
            .OrderBy(i => i.CreatedAtUtc).ThenBy(i => i.ChildAgentRunId).Take(limit).ToArrayAsync(ct).ConfigureAwait(false);
        var ids = items.Select(i => i.ChildAgentRunId).ToArray();
        var rows = await db.AgentRuns.AsNoTracking().Where(r => ids.Contains(r.AgentRunId)).OrderBy(r => r.UpdatedAtUtc).ThenBy(r => r.AgentRunId).ToArrayAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        var result = new List<BackgroundCompletionCandidate>();
        foreach (var row in rows)
        {
            var run = AgentRunStoreMapping.ToDomain(row);
            var child = await sessions.LoadMetadataAsync(run.SessionId, ct).ConfigureAwait(false);
            if (child is not null) result.Add(new(run, child));
        }
        return result;
    }

    public async ValueTask<IReadOnlyList<BackgroundCompletionCandidate>> ListUnreportedCompletionsAsync(int limit, CancellationToken ct = default)
    {
        if (limit is < 1 or > AgentRunLimits.MaxListLimit) throw AgentCoreErrors.Validation("Completion query requires a bounded limit.");
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await RepairInboxAsync(db, DateTimeOffset.MinValue, ct).ConfigureAwait(false);
        await SaveAsync(db, ct).ConfigureAwait(false);
        var pending = (await db.BackgroundCompletionReceipts.AsNoTracking().Where(r => r.Status == (int)CompletionInboxStatus.Pending).OrderBy(r => r.CreatedAtUtc).ThenBy(r => r.ChildAgentRunId).Take(limit).ToArrayAsync(ct).ConfigureAwait(false))
            .Where(r => CompletionInboxMapping.Read(r).Status == CompletionInboxStatus.Pending).Take(limit).Select(r => r.ChildAgentRunId).ToArray();
        var rows = await db.AgentRuns.AsNoTracking().Where(r => pending.Contains(r.AgentRunId)).OrderBy(r => r.UpdatedAtUtc).ThenBy(r => r.AgentRunId).ToArrayAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
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
        if (receipt is not null)
        {
            var item = CompletionInboxMapping.Read(receipt);
            if (item.Status == CompletionInboxStatus.Handled) return new("handled", item.ParentSessionId, item.HandledByRunId, null);
            if (item.Status == CompletionInboxStatus.Claimed) return new("claimed", item.ParentSessionId, item.ClaimRunId, null);
            if (item.Status == CompletionInboxStatus.Pending) return new("pending", item.ParentSessionId, null, null);
        }
        return CompletionDeliveryProjection.Build(session, reportRow is null ? null : AgentRunStoreMapping.ToDomain(reportRow), receipt is not null, receipt?.SkipReason);
    }

    public async ValueTask<bool> HasCompletionReceiptAsync(AgentRunOwner owner, Guid childRunId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var receipt = await db.BackgroundCompletionReceipts.AsNoTracking().SingleOrDefaultAsync(receipt => receipt.ChildAgentRunId == childRunId.ToString("D")
            && receipt.AgentInstanceId == owner.AgentInstanceId.ToString("D") && receipt.ProfileId == owner.ProfileId.ToString("D"), ct).ConfigureAwait(false);
        return receipt is not null && CompletionInboxMapping.Read(receipt).IsAccounted;
    }
    public async ValueTask SkipCompletionReportAsync(AgentRunOwner owner, Guid childRunId, string reason, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.AgentRuns.AsNoTracking().SingleOrDefaultAsync(row => row.AgentRunId == childRunId.ToString("D")
            && row.AgentInstanceId == owner.AgentInstanceId.ToString("D") && row.ProfileId == owner.ProfileId.ToString("D"), ct).ConfigureAwait(false);
        if (row is null || !AgentRunStoreMapping.ToDomain(row).IsTerminal) throw AgentCoreErrors.NotFound("Completed child run was not found.");
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await RepairInboxAsync(db, now, ct).ConfigureAwait(false);
        var receipt = db.BackgroundCompletionReceipts.Local.SingleOrDefault(r => r.ChildAgentRunId == childRunId.ToString("D"))
            ?? await db.BackgroundCompletionReceipts.SingleOrDefaultAsync(r => r.ChildAgentRunId == childRunId.ToString("D"), ct).ConfigureAwait(false);
        var inserted = 0;
        if (receipt is not null && CompletionInboxMapping.Read(receipt).Status == CompletionInboxStatus.Pending)
        {
            var item = CompletionInboxMapping.Read(receipt);
            CompletionInboxMapping.Write(receipt, item with { Revision = item.Revision + 1, Status = CompletionInboxStatus.Skipped, SkipReason = reason });
            inserted = 1;
        }
        await SaveAsync(db, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        RuntimeTelemetry.RecordBackgroundSession(inserted == 1 ? "completion-skipped" : "completion-deduped");
    }
}
