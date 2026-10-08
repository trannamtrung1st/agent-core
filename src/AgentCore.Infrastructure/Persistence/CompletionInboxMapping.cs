using System.Text.Json;
using AgentCore.Application.Sessions;
using AgentCore.Application.Observability;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Persistence;

internal static class CompletionInboxMapping
{
    internal static CompletionInboxItem Read(BackgroundCompletionReceiptRecord row) =>
        JsonSerializer.Deserialize<CompletionInboxItem>(row.InboxJson ?? throw AgentCoreErrors.Persistence("Completion inbox payload is missing."), AgentRunStoreMapping.Json)!;

    internal static void Write(BackgroundCompletionReceiptRecord row, CompletionInboxItem item)
    {
        if (row.InboxJson is not null && item.Revision != row.Revision)
            RuntimeTelemetry.RecordBackgroundSession(item.Acknowledgment is not null ? "inbox-ack-intent" : item.Status switch {
                CompletionInboxStatus.Pending => "inbox-released", CompletionInboxStatus.Claimed => "inbox-claimed",
                CompletionInboxStatus.Handled => "inbox-handled", CompletionInboxStatus.DeliveryQueued => "inbox-delivery-queued",
                CompletionInboxStatus.Delivered => "inbox-delivered", _ => "inbox-skipped" });
        row.InboxJson = JsonSerializer.Serialize(item, AgentRunStoreMapping.Json);
        row.Revision = item.Revision;
        row.ParentSessionId = item.ParentSessionId.ToString("D");
        row.Status = (int)item.Status;
        row.ClaimRunId = item.ClaimRunId?.ToString("D");
        row.ClaimExpiresAtUtc = item.ClaimExpiresAtUtc?.ToUnixTimeMilliseconds();
        row.ParentActivationId = item.ReportActivationId?.ToString("D");
        row.SkipReason = item.SkipReason;
    }

    internal static BackgroundCompletionReceiptRecord Create(AgentRun run, SessionSnapshot child)
    {
        var row = new BackgroundCompletionReceiptRecord { ChildAgentRunId = run.AgentRunId.ToString("D"),
            AgentInstanceId = run.AgentInstanceId.ToString("D"), ProfileId = run.ProfileId.ToString("D"), CreatedAtUtc = run.UpdatedAtUtc.ToUnixTimeMilliseconds() };
        Write(row, new(run.AgentRunId, run.Owner, child.Origin.OriginatingSessionId!.Value, run.SessionId, run.UpdatedAtUtc,
            Status: run.Result?.OutcomeKind == AgentRunOutcomeKind.NoAction ? CompletionInboxStatus.Skipped : CompletionInboxStatus.Pending,
            SkipReason: run.Result?.OutcomeKind == AgentRunOutcomeKind.NoAction ? "quiet-outcome" : null));
        RuntimeTelemetry.RecordBackgroundSession(run.Result?.OutcomeKind == AgentRunOutcomeKind.NoAction ? "inbox-skipped" : "inbox-pending");
        return row;
    }

    internal static CompletionInboxItem Refresh(CompletionInboxItem item, IReadOnlyList<AgentRun> runs, DateTimeOffset now)
    {
        if (item.Status != CompletionInboxStatus.Claimed) return item;
        var parent = runs.SingleOrDefault(r => r.AgentRunId == item.ClaimRunId && r.Owner == item.Owner);
        // A durable wait retains ownership, but not an expired consumption lease.
        if (parent is null || parent.IsTerminal || parent.Status == AgentRunStatus.WaitingToRetry
            || item.ClaimExpiresAtUtc <= now || parent.Status == AgentRunStatus.Running && parent.Claim?.Generation != item.ClaimGeneration)
        { RuntimeTelemetry.RecordBackgroundSession("inbox-claim-expired"); return item.Release(); }
        return item;
    }

    internal static void RequireParent(AgentRun? parent, AgentRunOwner owner, Guid generation, DateTimeOffset now)
    {
        if (parent is null || parent.Owner != owner) throw AgentCoreErrors.NotFound("Parent Run was not found.");
        if (parent.CancellationRequested || parent.Status != AgentRunStatus.Running || parent.Claim?.Generation != generation || parent.Claim.LeaseExpiresAtUtc <= now)
            throw AgentCoreErrors.Conflict("Parent Run generation is stale.");
    }

    internal static void RequireReportAdmission(SessionSnapshot snapshot, IEnumerable<AgentRun> runs)
    {
        if (snapshot.PendingAgentInputIds.Count > 0 || runs.Any(r => r.SessionId == snapshot.SessionId && !r.IsTerminal))
            throw AgentCoreErrors.Conflict("Completion delivery waits for parent execution and pending user input.");
    }
}
