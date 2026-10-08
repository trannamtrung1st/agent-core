using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Execution;

public sealed record CompletionDeliveryState(string Status, Guid? TargetSessionId, Guid? ParentAgentRunId, string? Reason);

public static class CompletionDeliveryProjection
{
    public static CompletionDeliveryState Build(SessionSnapshot child, AgentRun? report, bool hasReceipt, string? skipReason)
    {
        var target = child.Origin.OriginatingSessionId;
        if (!child.Origin.ReportCompletionToOrigin) return new("notRequested", null, null, null);
        if (!hasReceipt) return new("pending", target, null, null);
        if (skipReason is not null) return new("skipped", target, null, skipReason);
        if (report is null) return new("failed", target, null, "report-unavailable");
        if (report.Status == AgentRunStatus.Completed && report.Result?.OutcomeEntryId is not null)
            return new("delivered", target, report.AgentRunId, null);
        return report.IsTerminal ? new("failed", target, report.AgentRunId, report.Failure?.Code ?? "report-cancelled")
            : new("admitted", target, report.AgentRunId, null);
    }
}
