using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    private long InputOrdinal(AgentRun run) => run.Admission.Activation.SourceEntryIds
        .Select(id => sessionBacking.FindEntry(run.SessionId, id)?.Sequence ?? long.MaxValue).DefaultIfEmpty(long.MaxValue).Min();
    private bool TenantInputBlocked(AgentRun run)
    {
        if (run.Status == AgentRunStatus.Running) return false; // Expired claims still require recovery.
        return sessionBacking.AgentRuns.Runs.Values.Any(other => other.Owner == run.Owner && other.SessionId == run.SessionId
            && other.AgentRunId != run.AgentRunId && !other.IsTerminal
            && (other.Status is AgentRunStatus.Running or AgentRunStatus.WaitingForSignal
                || run.Admission.OutputContract == AgentRunOutputContract.CompletionReport && other.Admission.OutputContract != AgentRunOutputContract.CompletionReport
                || (run.Admission.OutputContract == AgentRunOutputContract.CompletionReport) == (other.Admission.OutputContract == AgentRunOutputContract.CompletionReport)
                    && other.Status != AgentRunStatus.WaitingForApproval && (other.CreatedAtUtc.ToUnixTimeMilliseconds() < run.CreatedAtUtc.ToUnixTimeMilliseconds()
                    || other.CreatedAtUtc.ToUnixTimeMilliseconds() == run.CreatedAtUtc.ToUnixTimeMilliseconds() && (InputOrdinal(other) < InputOrdinal(run)
                        || InputOrdinal(other) == InputOrdinal(run) && other.AgentRunId.CompareTo(run.AgentRunId) < 0))));
    }
}
