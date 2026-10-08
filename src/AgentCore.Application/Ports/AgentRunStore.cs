using AgentCore.Application.Execution;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Ports;

public sealed record AgentRunPage(IReadOnlyList<AgentRun> Items, Guid? NextCursor, bool HasMore);

public sealed record BackgroundCompletionCandidate(AgentRun Run, SessionSnapshot Session);

public sealed record AgentRunAdmissionResult(bool Created, AgentRun Run);

/// <summary>Admits input, Activation and run together; there is no standalone Activation insert.</summary>
public interface IAgentRunStore
{
    ValueTask<AgentRunAdmissionResult> AdmitAsync(SessionSnapshot snapshot, long expectedSessionRevision,
        AgentRun run, CancellationToken cancellationToken = default);

    ValueTask<AgentRunAdmissionResult> AdmitImmediateAsync(SessionSnapshot snapshot, AgentRun run,
        Guid expectedParentGeneration, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<BackgroundCompletionCandidate>> ListUnreportedCompletionsAsync(int limit, CancellationToken cancellationToken = default);
    ValueTask<bool> HasCompletionReceiptAsync(AgentRunOwner owner, Guid childRunId, CancellationToken cancellationToken = default);
    ValueTask SkipCompletionReportAsync(AgentRunOwner owner, Guid childRunId, string reason, DateTimeOffset now, CancellationToken cancellationToken = default);
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

    ValueTask<AgentRunPage> ListPageAsync(AgentRunOwner owner, Guid? sessionId, Guid? before, int limit, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<AgentRun>> ListRunnableAsync(DateTimeOffset asOfUtc, int limit,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<Guid>> ListPendingInputSessionsAsync(int limit, CancellationToken cancellationToken = default);

    ValueTask<AgentRun> ApplyAsync(AgentRunOwner owner, Guid agentRunId, AgentRunCommand command,
        CancellationToken cancellationToken = default);
}
