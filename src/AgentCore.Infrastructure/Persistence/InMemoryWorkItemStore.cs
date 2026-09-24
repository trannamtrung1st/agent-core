using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Work;

namespace AgentCore.Infrastructure.Persistence;

public sealed class InMemoryWorkItemStore : IWorkItemStore
{
    private readonly InMemoryDurableState _state;

    public InMemoryWorkItemStore()
        : this(new InMemoryDurableState())
    {
    }

    internal InMemoryWorkItemStore(InMemoryDurableState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
    }

    internal bool CrashOnNextClearSideEffect { get; set; }

    public ValueTask<WorkItemCreateResult> CreateAsync(WorkItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_state.Gate)
        {
            if (!item.IsInitialQueued)
            {
                throw AgentCoreErrors.Validation("Only a new queued work item can be created.");
            }

            if (_state.WorkBySource.TryGetValue(item.Provenance.SourceOccurrenceId, out var existingId))
            {
                var existing = _state.WorkItems[existingId];
                if (!existing.Owner.Equals(item.Owner))
                {
                    throw AgentCoreErrors.Conflict("Source occurrence is already owned.");
                }

                return ValueTask.FromResult(new WorkItemCreateResult(WorkItemCreateKind.Existing, existing));
            }

            if (_state.WorkItems.ContainsKey(item.WorkItemId))
            {
                throw AgentCoreErrors.Conflict("Work item already exists.");
            }

            _state.WorkItems[item.WorkItemId] = item;
            _state.WorkBySource[item.Provenance.SourceOccurrenceId] = item.WorkItemId;
            return ValueTask.FromResult(new WorkItemCreateResult(WorkItemCreateKind.Created, item));
        }
    }

    public ValueTask<WorkItem?> GetAsync(WorkOwner owner, Guid workItemId, CancellationToken cancellationToken = default)
    {
        lock (_state.Gate)
        {
            return ValueTask.FromResult(Find(owner, workItemId));
        }
    }

    public ValueTask<WorkItem?> GetBySourceOccurrenceAsync(Guid sourceOccurrenceId, CancellationToken cancellationToken = default)
    {
        lock (_state.Gate)
        {
            if (!_state.WorkBySource.TryGetValue(sourceOccurrenceId, out var workItemId))
            {
                return ValueTask.FromResult<WorkItem?>(null);
            }

            return ValueTask.FromResult<WorkItem?>(_state.WorkItems[workItemId]);
        }
    }

    public ValueTask<IReadOnlyList<WorkItem>> ListAsync(WorkOwner owner, int limit, CancellationToken cancellationToken = default)
    {
        var take = Clamp(limit);
        lock (_state.Gate)
        {
            var items = _state.WorkItems.Values
                .Where(item => item.Owner.Equals(owner))
                .OrderByDescending(item => item.CreatedAtUtc)
                .ThenByDescending(item => item.WorkItemId)
                .Take(take)
                .ToArray();
            return ValueTask.FromResult<IReadOnlyList<WorkItem>>(items);
        }
    }

    public ValueTask<IReadOnlyList<WorkItem>> ListRunnableAsync(
        DateTimeOffset asOfUtc,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var take = Clamp(limit);
        lock (_state.Gate)
        {
            var items = _state.WorkItems.Values
                .Where(item => IsRunnable(item, asOfUtc))
                .OrderBy(item => item.CreatedAtUtc)
                .ThenBy(item => item.WorkItemId)
                .Take(take)
                .ToArray();
            return ValueTask.FromResult<IReadOnlyList<WorkItem>>(items);
        }
    }

    public ValueTask<WorkItem?> TryClaimAsync(
        Guid workItemId,
        Guid generation,
        DateTimeOffset claimedAtUtc,
        DateTimeOffset leaseExpiresAtUtc,
        CancellationToken cancellationToken = default)
    {
        lock (_state.Gate)
        {
            if (!_state.WorkItems.TryGetValue(workItemId, out var current))
            {
                return ValueTask.FromResult<WorkItem?>(null);
            }

            try
            {
                var updated = current.TakeClaim(generation, claimedAtUtc, leaseExpiresAtUtc);
                _state.WorkItems[workItemId] = updated;
                return ValueTask.FromResult<WorkItem?>(updated);
            }
            catch (WorkItemTransitionException exception) when (exception.Failure == WorkTransitionFailure.NotClaimable)
            {
                return ValueTask.FromResult<WorkItem?>(null);
            }
            catch (Exception exception) when (exception is WorkItemTransitionException or ArgumentException)
            {
                throw WorkStoreMapping.Map(exception);
            }
        }
    }

    public ValueTask<WorkItem> RenewClaimAsync(
        Guid workItemId,
        long expectedRevision,
        Guid generation,
        DateTimeOffset leaseExpiresAtUtc,
        DateTimeOffset renewedAtUtc,
        CancellationToken cancellationToken = default) =>
        Mutate(workItemId, item => item.RenewClaim(expectedRevision, generation, leaseExpiresAtUtc, renewedAtUtc));

    public ValueTask<WorkItem> CheckpointAsync(
        Guid workItemId,
        long expectedRevision,
        Guid generation,
        WorkCheckpoint checkpoint,
        string? progressSummary,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default) =>
        Mutate(workItemId, item => item.SaveCheckpoint(expectedRevision, generation, checkpoint, progressSummary, updatedAtUtc));

    public ValueTask<WorkItem> CompleteAsync(
        Guid workItemId,
        long expectedRevision,
        Guid generation,
        string resultText,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken = default) =>
        Mutate(workItemId, item => item.Complete(expectedRevision, generation, resultText, completedAtUtc));

    public ValueTask<WorkItem> FailAsync(
        Guid workItemId,
        long expectedRevision,
        Guid generation,
        string failureCode,
        string failureSummary,
        bool replaySafe,
        DateTimeOffset failedAtUtc,
        DateTimeOffset? nextRetryAtUtc,
        CancellationToken cancellationToken = default) =>
        Mutate(workItemId, item => item.Fail(
            expectedRevision,
            generation,
            failureCode,
            failureSummary,
            replaySafe,
            failedAtUtc,
            nextRetryAtUtc));

    public ValueTask<WorkItem> RequestCancellationAsync(
        WorkOwner owner,
        Guid workItemId,
        long expectedRevision,
        string? knownEffectSummary,
        DateTimeOffset requestedAtUtc,
        CancellationToken cancellationToken = default) =>
        Mutate(workItemId, item => item.RequestCancellation(expectedRevision, knownEffectSummary, requestedAtUtc), owner);

    public ValueTask<WorkItem> CommitCancellationAsync(
        Guid workItemId,
        long expectedRevision,
        Guid generation,
        string? knownEffectSummary,
        DateTimeOffset cancelledAtUtc,
        CancellationToken cancellationToken = default) =>
        Mutate(workItemId, item => item.CommitCancellation(expectedRevision, generation, knownEffectSummary, cancelledAtUtc));

    public ValueTask<int> RecoverExpiredClaimsAsync(DateTimeOffset asOfUtc, CancellationToken cancellationToken = default)
    {
        lock (_state.Gate)
        {
            var expired = _state.WorkItems.Values
                .Where(item => item.Status == WorkItemStatus.Running
                    && item.Claim is not null
                    && item.Claim.LeaseExpiresAtUtc <= asOfUtc)
                .ToArray();
            foreach (var item in expired)
            {
                try
                {
                    _state.WorkItems[item.WorkItemId] = item.RecoverExpiredClaim(asOfUtc);
                }
                catch (Exception exception) when (exception is WorkItemTransitionException or ArgumentException)
                {
                    throw WorkStoreMapping.Map(exception);
                }
            }

            return ValueTask.FromResult(expired.Length);
        }
    }

    public ValueTask<int> ExpireDueApprovalsAsync(DateTimeOffset asOfUtc, CancellationToken cancellationToken = default)
    {
        lock (_state.Gate)
        {
            var due = _state.WorkItems.Values
                .Where(item => item.Status == WorkItemStatus.WaitingForApproval
                    && item.Approval is { Decision: WorkApprovalDecision.Pending }
                    && item.Approval.ExpiresAtUtc <= asOfUtc)
                .ToArray();
            foreach (var item in due)
            {
                try
                {
                    _state.WorkItems[item.WorkItemId] = item.ExpireApproval(item.Revision, asOfUtc);
                }
                catch (Exception exception) when (exception is WorkItemTransitionException or ArgumentException)
                {
                    throw WorkStoreMapping.Map(exception);
                }
            }

            return ValueTask.FromResult(due.Length);
        }
    }

    public ValueTask<WorkItem> BeginApprovalAsync(
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
        CancellationToken cancellationToken = default) =>
        Mutate(workItemId, item => item.BeginApproval(
            expectedRevision,
            generation,
            approvalId,
            toolName,
            preparedActionJson,
            actionHash,
            preview,
            expiresAtUtc,
            updatedAtUtc));

    public ValueTask<WorkItem> DecideApprovalAsync(
        WorkOwner owner,
        Guid workItemId,
        Guid approvalId,
        long expectedRevision,
        long expectedApprovalRevision,
        string actionHash,
        WorkApprovalDecision decision,
        DateTimeOffset decidedAtUtc,
        CancellationToken cancellationToken = default) =>
        Mutate(
            workItemId,
            item => item.DecideApproval(approvalId, expectedRevision, expectedApprovalRevision, actionHash, decision, decidedAtUtc),
            owner);

    public ValueTask<WorkItem> ExpireApprovalAsync(
        Guid workItemId,
        long expectedRevision,
        DateTimeOffset expiredAtUtc,
        CancellationToken cancellationToken = default) =>
        Mutate(workItemId, item => item.ExpireApproval(expectedRevision, expiredAtUtc));

    public ValueTask<WorkItem> MarkSideEffectAsync(
        Guid workItemId,
        long expectedRevision,
        Guid generation,
        WorkSideEffectDisposition disposition,
        string actionHash,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default) =>
        Mutate(workItemId, item => item.MarkSideEffect(expectedRevision, generation, disposition, actionHash, updatedAtUtc));

    public ValueTask<WorkItem> ClearSideEffectAsync(
        Guid workItemId,
        long expectedRevision,
        Guid generation,
        DateTimeOffset clearedAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (CrashOnNextClearSideEffect)
        {
            CrashOnNextClearSideEffect = false;
            throw new InvalidOperationException("Simulated crash before side-effect clear.");
        }

        return Mutate(workItemId, item => item.ClearSideEffect(expectedRevision, generation, clearedAtUtc));
    }

    private ValueTask<WorkItem> Mutate(Guid workItemId, Func<WorkItem, WorkItem> change, WorkOwner? owner = null)
    {
        lock (_state.Gate)
        {
            if (!_state.WorkItems.TryGetValue(workItemId, out var current) || (owner is WorkOwner required && !current.Owner.Equals(required)))
            {
                throw AgentCoreErrors.NotFound("Work item was not found.");
            }

            try
            {
                var updated = change(current);
                _state.WorkItems[workItemId] = updated;
                return ValueTask.FromResult(updated);
            }
            catch (Exception exception) when (exception is WorkItemTransitionException or ArgumentException)
            {
                throw WorkStoreMapping.Map(exception);
            }
        }
    }

    private WorkItem? Find(WorkOwner owner, Guid workItemId) =>
        _state.WorkItems.TryGetValue(workItemId, out var item) && item.Owner.Equals(owner) ? item : null;

    private static bool IsRunnable(WorkItem item, DateTimeOffset asOfUtc) =>
        item.Status == WorkItemStatus.Queued
        || (item.Status == WorkItemStatus.WaitingToRetry && item.NextRetryAtUtc <= asOfUtc);

    private static int Clamp(int limit) => Math.Clamp(limit, 1, WorkLimits.MaxListLimit);
}
