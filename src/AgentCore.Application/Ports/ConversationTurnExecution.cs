using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Ports;

public enum ConversationTurnExecutionCreateKind
{
    Created = 0,
    Existing = 1
}

public sealed record ConversationTurnExecutionCreateResult(
    ConversationTurnExecutionCreateKind Kind,
    ConversationTurnExecution Item);

public interface IConversationTurnExecutionStore
{
    ValueTask<ConversationTurnExecutionCreateResult> CreateAsync(
        ConversationTurnExecution item,
        CancellationToken cancellationToken = default);

    ValueTask<ConversationTurnExecution?> GetAsync(Guid executionId, CancellationToken cancellationToken = default);

    ValueTask<ConversationTurnExecution?> GetBySourceEventAsync(
        Guid sessionId,
        Guid sourceEventId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<ConversationTurnExecution>> ListOpenForSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<ConversationTurnExecution>> ListRunnableAsync(
        DateTimeOffset asOfUtc,
        int limit,
        CancellationToken cancellationToken = default);

    ValueTask<ConversationTurnExecution?> TryClaimAsync(
        Guid executionId,
        Guid generation,
        DateTimeOffset claimedAtUtc,
        DateTimeOffset leaseExpiresAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<ConversationTurnExecution> RenewClaimAsync(
        Guid executionId,
        long expectedRevision,
        Guid generation,
        DateTimeOffset leaseExpiresAtUtc,
        DateTimeOffset renewedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<ConversationTurnExecution> AttachAssistantAsync(
        Guid executionId,
        long expectedRevision,
        Guid generation,
        Guid assistantEntryId,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<ConversationTurnExecution> CompleteAsync(
        Guid executionId,
        long expectedRevision,
        Guid generation,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<ConversationTurnExecution> FailAsync(
        Guid executionId,
        long expectedRevision,
        Guid generation,
        DateTimeOffset failedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<ConversationTurnExecution> CommitCancellationAsync(
        Guid executionId,
        long expectedRevision,
        Guid generation,
        DateTimeOffset cancelledAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<ConversationTurnExecution> MarkWaitingForApprovalAsync(
        Guid executionId,
        long expectedRevision,
        Guid generation,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<ConversationTurnExecution> ResumeRunningAsync(
        Guid executionId,
        long expectedRevision,
        Guid generation,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<ConversationTurnExecution> RequestCancellationAsync(
        Guid sessionId,
        Guid executionId,
        long expectedRevision,
        DateTimeOffset requestedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<int> RecoverExpiredClaimsAsync(DateTimeOffset asOfUtc, CancellationToken cancellationToken = default);
}
