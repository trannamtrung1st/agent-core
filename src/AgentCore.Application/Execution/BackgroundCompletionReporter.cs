using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Execution;

public sealed class BackgroundCompletionReporter(IAgentRunStore runs, IMemoryStore memory,
    IAgentRunDispatcher dispatcher, TimeProvider time)
{
    public async ValueTask ReportPendingAsync(int limit, CancellationToken ct = default)
    {
        foreach (var source in await runs.ListUnreportedCompletionsAsync(limit, ct).ConfigureAwait(false))
        {
            var parent = source.Session.Origin.OriginatingSessionId is { } id ? await memory.LoadMetadataAsync(id, ct).ConfigureAwait(false) : null;
            var eligible = source.Session.DurablyDeletedAt is null && parent is not null && parent.DurablyDeletedAt is null
                && parent.ArchivedAt is null && parent.AgentInstanceId == source.Run.AgentInstanceId && parent.ProfileId == source.Run.ProfileId
                && parent.Status is not (SessionStatus.Ended or SessionStatus.Ending)
                && !SessionLifecycle.IsTerminal(parent.LifecycleStatus)
                && (parent.Status != SessionStatus.Paused || SessionPauseSemantics.IsTransportResumable(parent.PauseReason));
            if (!eligible || source.Run.Result?.OutcomeKind == AgentRunOutcomeKind.NoAction)
            {
                await runs.SkipCompletionReportAsync(source.Run.Owner, source.Run.AgentRunId,
                    eligible ? "quiet-outcome" : "parent-unavailable", time.GetUtcNow(), ct).ConfigureAwait(false);
                continue;
            }
            // Capacity and transport failures retain the source for a later bounded pass.
            await dispatcher.AdmitCompletionAsync(source, ct).ConfigureAwait(false);
        }
    }
}
