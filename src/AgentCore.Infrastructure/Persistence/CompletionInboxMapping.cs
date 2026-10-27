using System.Text.Json;
using AgentCore.Application.Sessions;
using AgentCore.Application.Observability;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Persistence;

internal static class CompletionInboxMapping
{
    internal static CompletionInboxItem Read(BackgroundCompletionReceiptRecord row)
    {
        var item = JsonSerializer.Deserialize<CompletionInboxItem>(row.InboxJson ?? throw AgentCoreErrors.Persistence("Completion receipt payload is missing."), AgentRunStoreMapping.Json)
            ?? throw AgentCoreErrors.Persistence("Completion receipt payload is missing.");
        if (!Enum.IsDefined(item.Status)) throw AgentCoreErrors.Persistence("Retired completion consumption is unsupported.");
        return item;
    }

    internal static void Write(BackgroundCompletionReceiptRecord row, CompletionInboxItem item)
    {
        if (row.InboxJson is not null && item.Revision != row.Revision)
            RuntimeTelemetry.RecordBackgroundSession(item.Status switch {
                CompletionInboxStatus.Pending => "inbox-pending", CompletionInboxStatus.DeliveryQueued => "inbox-delivery-queued",
                CompletionInboxStatus.Delivered => "inbox-delivered", _ => "inbox-skipped" });
        row.InboxJson = JsonSerializer.Serialize(item, AgentRunStoreMapping.Json);
        row.Revision = item.Revision;
        row.ParentSessionId = item.ParentSessionId.ToString("D");
        row.Status = (int)item.Status;
        row.ClaimRunId = null;
        row.ClaimExpiresAtUtc = null;
        row.ParentActivationId = item.ReportActivationId?.ToString("D");
        row.SkipReason = item.SkipReason;
    }

    internal static BackgroundCompletionReceiptRecord Create(AgentRun run, SessionSnapshot child)
    {
        var row = new BackgroundCompletionReceiptRecord { ChildAgentRunId = run.AgentRunId.ToString("D"),
            AgentInstanceId = run.AgentInstanceId.ToString("D"), ProfileId = run.Owner.ProfileIdOrNull?.ToString("D"), CreatedAtUtc = run.UpdatedAtUtc.ToUnixTimeMilliseconds() };
        Write(row, new(run.AgentRunId, run.Owner, child.Origin.OriginatingSessionId!.Value, run.SessionId, run.UpdatedAtUtc,
            Status: run.Result?.OutcomeKind == AgentRunOutcomeKind.NoAction ? CompletionInboxStatus.Skipped : CompletionInboxStatus.Pending,
            SkipReason: run.Result?.OutcomeKind == AgentRunOutcomeKind.NoAction ? "quiet-outcome" : null));
        RuntimeTelemetry.RecordBackgroundSession(run.Result?.OutcomeKind == AgentRunOutcomeKind.NoAction ? "inbox-skipped" : "inbox-pending");
        return row;
    }

    internal static void RequireReportAdmission(SessionSnapshot snapshot, IEnumerable<AgentRun> runs)
    {
        if (snapshot.PendingAgentInputIds.Count > 0 || runs.Any(r => r.SessionId == snapshot.SessionId && !r.IsTerminal))
            throw AgentCoreErrors.Conflict("Completion delivery waits for parent execution and pending user input.");
    }
}
