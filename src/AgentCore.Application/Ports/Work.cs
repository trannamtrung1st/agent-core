using AgentCore.Domain.Work;

namespace AgentCore.Application.Ports;

public enum WorkItemCreateKind
{
    Created = 0,
    Existing = 1
}

public sealed record WorkItemCreateResult(WorkItemCreateKind Kind, WorkItem Item);

public interface IWorkItemStore
{
    ValueTask<WorkItemCreateResult> CreateAsync(WorkItem item, CancellationToken cancellationToken = default);

    ValueTask<WorkItem?> GetAsync(WorkOwner owner, Guid workItemId, CancellationToken cancellationToken = default);

    ValueTask<WorkItem?> GetBySourceOccurrenceAsync(Guid sourceOccurrenceId, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<WorkItem>> ListAsync(WorkOwner owner, int limit, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<WorkItem>> ListRunnableAsync(DateTimeOffset asOfUtc, int limit, CancellationToken cancellationToken = default);

    ValueTask<WorkItem?> TryClaimAsync(
        Guid workItemId,
        Guid generation,
        DateTimeOffset claimedAtUtc,
        DateTimeOffset leaseExpiresAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<WorkItem> RenewClaimAsync(
        Guid workItemId,
        long expectedRevision,
        Guid generation,
        DateTimeOffset leaseExpiresAtUtc,
        DateTimeOffset renewedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<WorkItem> CheckpointAsync(
        Guid workItemId,
        long expectedRevision,
        Guid generation,
        WorkCheckpoint checkpoint,
        string? progressSummary,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<WorkItem> CompleteAsync(
        Guid workItemId,
        long expectedRevision,
        Guid generation,
        string resultText,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<WorkItem> FailAsync(
        Guid workItemId,
        long expectedRevision,
        Guid generation,
        string failureCode,
        string failureSummary,
        bool replaySafe,
        DateTimeOffset failedAtUtc,
        DateTimeOffset? nextRetryAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<WorkItem> RequestCancellationAsync(
        WorkOwner owner,
        Guid workItemId,
        long expectedRevision,
        string? knownEffectSummary,
        DateTimeOffset requestedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<WorkItem> CommitCancellationAsync(
        Guid workItemId,
        long expectedRevision,
        Guid generation,
        string? knownEffectSummary,
        DateTimeOffset cancelledAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<int> RecoverExpiredClaimsAsync(DateTimeOffset asOfUtc, CancellationToken cancellationToken = default);

    ValueTask<WorkItem> BeginApprovalAsync(
        Guid workItemId,
        long expectedRevision,
        Guid generation,
        Guid approvalId,
        string toolName,
        string preparedActionJson,
        string actionHash,
        string preview,
        DateTimeOffset expiresAtUtc,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<WorkItem> DecideApprovalAsync(
        WorkOwner owner,
        Guid workItemId,
        Guid approvalId,
        long expectedRevision,
        long expectedApprovalRevision,
        string actionHash,
        WorkApprovalDecision decision,
        DateTimeOffset decidedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<WorkItem> ExpireApprovalAsync(
        Guid workItemId,
        long expectedRevision,
        DateTimeOffset expiredAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<WorkItem> MarkSideEffectAsync(
        Guid workItemId,
        long expectedRevision,
        Guid generation,
        WorkSideEffectDisposition disposition,
        string actionHash,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default);
}

public interface IDurableWorkHandoff
{
    ValueTask<WorkItemCreateResult> AcceptAsync(
        Guid occurrenceId,
        WorkItem proposed,
        DateTimeOffset acceptedAtUtc,
        CancellationToken cancellationToken = default);
}
