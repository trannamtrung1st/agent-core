using AgentCore.Application.Execution;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Ports;

public sealed record AgentRunPage(IReadOnlyList<AgentRun> Items, Guid? NextCursor, bool HasMore);

public sealed record BackgroundCompletionCandidate(AgentRun Run, SessionSnapshot Session);

public sealed record AgentRunAdmissionResult(bool Created, AgentRun Run);

/// <summary>Admits input, Activation and run together; there is no standalone Activation insert.</summary>
public interface IAgentRunStore
{
    ValueTask<CompletionInboxItem?> GetCompletionInboxAsync(AgentRunOwner owner, Guid parentSessionId, Guid childRunId, DateTimeOffset now, CancellationToken ct = default) => ValueTask.FromResult<CompletionInboxItem?>(null);
    ValueTask<bool> HasCompletionClaimAsync(AgentRunOwner owner, Guid parentRunId, Guid generation, DateTimeOffset now, CancellationToken ct = default) => ValueTask.FromResult(false);
    ValueTask<IReadOnlyList<BackgroundCompletionCandidate>> ListBackgroundPageAsync(AgentRunOwner owner, Guid parentSessionId, Guid? cursor, bool pendingOnly, int limit, CancellationToken ct = default) => ListBackgroundChildrenAsync(owner, parentSessionId, limit, ct);
    ValueTask<bool> HasCompletionAcknowledgmentAsync(AgentRunOwner owner, Guid parentRunId, CancellationToken ct = default) => ValueTask.FromResult(false);
    ValueTask<IReadOnlyList<BackgroundCompletionCandidate>> ListBackgroundChildrenAsync(AgentRunOwner owner, Guid parentSessionId, int limit, CancellationToken ct = default) => throw new NotSupportedException();
    ValueTask<IReadOnlyList<CompletionInboxItem>> ListCompletionInboxAsync(AgentRunOwner owner, Guid parentSessionId, int limit, DateTimeOffset now, CancellationToken ct = default) => throw new NotSupportedException();
    ValueTask<CompletionInboxItem> TakeCompletionAsync(AgentRunOwner owner, Guid parentRunId, Guid generation, Guid childRunId, long expectedRevision, string toolCallId, Guid token, DateTimeOffset now, CancellationToken ct = default) => throw new NotSupportedException();
    ValueTask<CompletionInboxItem> AcknowledgeCompletionAsync(AgentRunOwner owner, Guid parentRunId, Guid generation, Guid childRunId, long expectedRevision, Guid token, string usage, DateTimeOffset now, CancellationToken ct = default) => throw new NotSupportedException();

    ValueTask<AgentRunAdmissionResult> AdmitAsync(SessionSnapshot snapshot, long expectedSessionRevision,
        AgentRun run, CancellationToken cancellationToken = default);

    ValueTask<AgentRunAdmissionResult> AdmitImmediateAsync(SessionSnapshot snapshot, AgentRun run,
        Guid expectedParentGeneration, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<BackgroundCompletionCandidate>> ListDeliveryCandidatesAsync(int limit, DateTimeOffset now, CancellationToken ct = default) => ListUnreportedCompletionsAsync(limit, ct);
    ValueTask<IReadOnlyList<BackgroundCompletionCandidate>> ListUnreportedCompletionsAsync(int limit, CancellationToken cancellationToken = default);
    ValueTask<CompletionDeliveryState> GetCompletionDeliveryAsync(AgentRunOwner owner, Guid childRunId, CancellationToken ct = default) => throw new NotSupportedException();
    ValueTask<bool> HasCompletionReceiptAsync(AgentRunOwner owner, Guid childRunId, CancellationToken cancellationToken = default);
    ValueTask SkipCompletionReportAsync(AgentRunOwner owner, Guid childRunId, string reason, DateTimeOffset now, CancellationToken cancellationToken = default);
    ValueTask<AgentRunAdmissionResult> AdmitCompletionReportBatchAsync(SessionSnapshot parent, long expectedRevision, AgentRun report,
        IReadOnlyList<Guid> childRunIds, CancellationToken ct = default) => childRunIds.Count == 1 ? AdmitCompletionReportAsync(parent, expectedRevision, report, childRunIds[0], ct) : throw new NotSupportedException();
    ValueTask<AgentRunAdmissionResult> AdmitCompletionReportAsync(SessionSnapshot parent, long expectedRevision, AgentRun report,
        Guid childRunId, CancellationToken cancellationToken = default);

    ValueTask<AgentRun> CommitOutcomeAsync(SessionSnapshot snapshot, long expectedSessionRevision,
        AgentRunOwner owner, Guid agentRunId, AgentRunCommand.Complete completion, Guid? draftEntryId,
        CancellationToken cancellationToken = default);

    ValueTask<AgentRunAdmissionResult> AdmitOccurrenceAsync(SessionSnapshot snapshot, AgentRun run,
        long expectedRoutingRevision, CancellationToken cancellationToken = default);

    ValueTask<AgentRun?> GetAsync(AgentRunOwner owner, Guid agentRunId, CancellationToken cancellationToken = default);

    ValueTask<Activation?> GetActivationAsync(AgentRunOwner owner, Guid activationId, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<AgentRun>> ListForSessionAsync(AgentRunOwner owner, Guid sessionId,
        CancellationToken cancellationToken = default);

    ValueTask<AgentRun?> GetLatestForAutomationAsync(AgentRunOwner owner, Guid automationId, CancellationToken cancellationToken = default);

    // Inspection pages require a matching, undeleted Session; archived Sessions remain visible.
    // Resolve the exclusive cursor within the retained owner/Session Run scope before visibility filtering.
    ValueTask<AgentRunPage> ListPageAsync(AgentRunOwner owner, Guid? sessionId, Guid? before, int limit, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<AgentRun>> ListRunnableAsync(DateTimeOffset asOfUtc, int limit,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<Guid>> ListPendingInputSessionsAsync(int limit, CancellationToken cancellationToken = default);

    ValueTask<AgentRun> ApplyAsync(AgentRunOwner owner, Guid agentRunId, AgentRunCommand command,
        CancellationToken cancellationToken = default);
}
