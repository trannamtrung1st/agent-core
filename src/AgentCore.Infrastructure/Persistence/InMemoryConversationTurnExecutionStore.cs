using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Work;

namespace AgentCore.Infrastructure.Persistence;

public sealed class InMemoryConversationTurnExecutionStore : IConversationTurnExecutionStore
{
    private readonly InMemoryDurableState _state;

    public InMemoryConversationTurnExecutionStore()
        : this(new InMemoryDurableState())
    {
    }

    internal int CountForInstance(Guid agentInstanceId)
    {
        lock (_state.Gate)
        {
            return _state.TurnExecutions.Values.Count(item => item.AgentInstanceId == agentInstanceId);
        }
    }

    internal int CountForDefinition(string definitionId)
    {
        lock (_state.Gate)
        {
            return _state.TurnExecutions.Values.Count(item =>
                string.Equals(item.DefinitionId, definitionId, StringComparison.Ordinal));
        }
    }

    internal InMemoryConversationTurnExecutionStore(InMemoryDurableState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
    }

    public ValueTask<ConversationTurnExecutionCreateResult> CreateAsync(
        ConversationTurnExecution item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_state.Gate)
        {
            if (!item.IsInitialQueued)
            {
                throw AgentCoreErrors.Validation("Only a new queued turn execution can be created.");
            }

            var sourceKey = new InMemoryDurableState.SourceEventIdentity(item.SessionId, item.SourceEventId);
            if (_state.TurnExecutionBySource.TryGetValue(sourceKey, out var existingId))
            {
                return ValueTask.FromResult(
                    new ConversationTurnExecutionCreateResult(
                        ConversationTurnExecutionCreateKind.Existing,
                        _state.TurnExecutions[existingId]));
            }

            if (_state.TurnExecutions.ContainsKey(item.ExecutionId))
            {
                throw AgentCoreErrors.Conflict("Turn execution already exists.");
            }

            _state.TurnExecutions[item.ExecutionId] = item;
            _state.TurnExecutionBySource[sourceKey] = item.ExecutionId;
            return ValueTask.FromResult(
                new ConversationTurnExecutionCreateResult(ConversationTurnExecutionCreateKind.Created, item));
        }
    }

    public ValueTask<ConversationTurnExecution?> GetAsync(Guid executionId, CancellationToken cancellationToken = default)
    {
        lock (_state.Gate)
        {
            return ValueTask.FromResult(
                _state.TurnExecutions.TryGetValue(executionId, out var item) ? item : null);
        }
    }

    public ValueTask<ConversationTurnExecution?> GetBySourceEventAsync(
        Guid sessionId,
        Guid sourceEventId,
        CancellationToken cancellationToken = default)
    {
        lock (_state.Gate)
        {
            var key = new InMemoryDurableState.SourceEventIdentity(sessionId, sourceEventId);
            if (!_state.TurnExecutionBySource.TryGetValue(key, out var executionId))
            {
                return ValueTask.FromResult<ConversationTurnExecution?>(null);
            }

            return ValueTask.FromResult<ConversationTurnExecution?>(_state.TurnExecutions[executionId]);
        }
    }

    public ValueTask<IReadOnlyList<ConversationTurnExecution>> ListOpenForSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        lock (_state.Gate)
        {
            var items = _state.TurnExecutions.Values
                .Where(item => item.SessionId == sessionId && item.IsOpen)
                .OrderBy(item => item.AcceptedAtUtc)
                .ToArray();
            return ValueTask.FromResult<IReadOnlyList<ConversationTurnExecution>>(items);
        }
    }

    public ValueTask<IReadOnlyList<ConversationTurnExecution>> ListRunnableAsync(
        DateTimeOffset asOfUtc,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var take = Math.Clamp(limit, 1, 100);
        lock (_state.Gate)
        {
            var items = _state.TurnExecutions.Values
                .Where(item => item.Status == ConversationTurnExecutionStatus.Queued)
                .OrderBy(item => item.AcceptedAtUtc)
                .ThenBy(item => item.ExecutionId)
                .Take(take)
                .ToArray();
            return ValueTask.FromResult<IReadOnlyList<ConversationTurnExecution>>(items);
        }
    }

    public ValueTask<ConversationTurnExecution> PinActiveSkillsAsync(
        Guid executionId,
        long expectedRevision,
        IReadOnlyList<string> skillIds,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default) =>
        Mutate(executionId, item => item.PinActiveSkillsBeforeStart(expectedRevision, skillIds, updatedAtUtc));

    public ValueTask<ConversationTurnExecution> AdmitActiveSkillsAsync(
        Guid executionId,
        long expectedRevision,
        Guid generation,
        IReadOnlyList<string> skillIds,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default) =>
        Mutate(
            executionId,
            item => item.AdmitActiveSkills(expectedRevision, generation, skillIds, updatedAtUtc));

    public ValueTask<ConversationTurnExecution?> TryClaimAsync(
        Guid executionId,
        Guid generation,
        DateTimeOffset claimedAtUtc,
        DateTimeOffset leaseExpiresAtUtc,
        CancellationToken cancellationToken = default)
    {
        lock (_state.Gate)
        {
            if (!_state.TurnExecutions.TryGetValue(executionId, out var current))
            {
                return ValueTask.FromResult<ConversationTurnExecution?>(null);
            }

            try
            {
                var updated = current.TakeClaim(generation, claimedAtUtc, leaseExpiresAtUtc);
                _state.TurnExecutions[executionId] = updated;
                return ValueTask.FromResult<ConversationTurnExecution?>(updated);
            }
            catch (WorkItemTransitionException exception) when (exception.Failure == WorkTransitionFailure.NotClaimable)
            {
                return ValueTask.FromResult<ConversationTurnExecution?>(null);
            }
            catch (Exception exception) when (exception is WorkItemTransitionException or ArgumentException)
            {
                throw ConversationTurnExecutionStoreMapping.Map(exception);
            }
        }
    }

    public ValueTask<ConversationTurnExecution> RenewClaimAsync(
        Guid executionId,
        long expectedRevision,
        Guid generation,
        DateTimeOffset leaseExpiresAtUtc,
        DateTimeOffset renewedAtUtc,
        CancellationToken cancellationToken = default) =>
        Mutate(executionId, item => item.RenewClaim(expectedRevision, generation, leaseExpiresAtUtc, renewedAtUtc));

    public ValueTask<ConversationTurnExecution> AttachAssistantAsync(
        Guid executionId,
        long expectedRevision,
        Guid generation,
        Guid assistantEntryId,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default) =>
        Mutate(
            executionId,
            item =>
            {
                item.RequireOperational(expectedRevision, generation);
                return item.WithAssistant(assistantEntryId, updatedAtUtc);
            });

    public ValueTask<ConversationTurnExecution> CompleteAsync(
        Guid executionId,
        long expectedRevision,
        Guid generation,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken = default) =>
        Mutate(executionId, item => item.CompleteTerminal(expectedRevision, generation, completedAtUtc));

    public ValueTask<ConversationTurnExecution> FailAsync(
        Guid executionId,
        long expectedRevision,
        Guid generation,
        DateTimeOffset failedAtUtc,
        CancellationToken cancellationToken = default) =>
        Mutate(executionId, item => item.FailTerminal(expectedRevision, generation, failedAtUtc));

    public ValueTask<ConversationTurnExecution> CommitCancellationAsync(
        Guid executionId,
        long expectedRevision,
        Guid generation,
        DateTimeOffset cancelledAtUtc,
        CancellationToken cancellationToken = default) =>
        Mutate(executionId, item => item.CommitCancellation(expectedRevision, generation, cancelledAtUtc));

    public ValueTask<ConversationTurnExecution> MarkWaitingForApprovalAsync(
        Guid executionId,
        long expectedRevision,
        Guid generation,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default) =>
        Mutate(
            executionId,
            item =>
            {
                item.RequireOperational(expectedRevision, generation);
                return item.WithStatus(ConversationTurnExecutionStatus.WaitingForApproval, updatedAtUtc);
            });

    public ValueTask<ConversationTurnExecution> ResumeRunningAsync(
        Guid executionId,
        long expectedRevision,
        Guid generation,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default) =>
        Mutate(
            executionId,
            item =>
            {
                item.RequireOperational(expectedRevision, generation);
                return item.WithStatus(ConversationTurnExecutionStatus.Running, updatedAtUtc);
            });

    public ValueTask<ConversationTurnExecution> RequestCancellationAsync(
        Guid sessionId,
        Guid executionId,
        long expectedRevision,
        DateTimeOffset requestedAtUtc,
        CancellationToken cancellationToken = default) =>
        Mutate(
            executionId,
            item =>
            {
                if (item.SessionId != sessionId)
                {
                    throw AgentCoreErrors.NotFound("Turn execution was not found.");
                }

                item.RequireOperational(expectedRevision, item.Claim?.Generation ?? Guid.Empty);
                return item.WithCancellationRequested(requestedAtUtc);
            });

    public ValueTask<int> RecoverExpiredClaimsAsync(DateTimeOffset asOfUtc, CancellationToken cancellationToken = default)
    {
        lock (_state.Gate)
        {
            var expired = _state.TurnExecutions.Values
                .Where(item => item.Status == ConversationTurnExecutionStatus.Running
                    && item.Claim is not null
                    && item.Claim.LeaseExpiresAtUtc <= asOfUtc)
                .ToArray();
            foreach (var item in expired)
            {
                _state.TurnExecutions[item.ExecutionId] = item.RequeueExpiredClaim(asOfUtc);
            }

            return ValueTask.FromResult(expired.Length);
        }
    }

    private ValueTask<ConversationTurnExecution> Mutate(
        Guid executionId,
        Func<ConversationTurnExecution, ConversationTurnExecution> mutate)
    {
        lock (_state.Gate)
        {
            if (!_state.TurnExecutions.TryGetValue(executionId, out var current))
            {
                throw AgentCoreErrors.NotFound("Turn execution was not found.");
            }

            try
            {
                var updated = mutate(current);
                _state.TurnExecutions[executionId] = updated;
                return ValueTask.FromResult(updated);
            }
            catch (Exception exception) when (exception is WorkItemTransitionException or ArgumentException)
            {
                throw ConversationTurnExecutionStoreMapping.Map(exception);
            }
        }
    }
}
