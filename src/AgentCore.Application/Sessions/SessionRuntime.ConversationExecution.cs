using AgentCore.Application.Conversation;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime
{
    private readonly IConversationTurnExecutionStore? _turnExecutions;
    private ConversationTurnExecution? _boundConversationExecution;
    private Guid? _pendingTerminalExecutionId;
    private Guid? _pendingTerminalResponseId;

    public async Task<bool> DispatchConversationExecutionAsync(
        Guid executionId,
        CancellationToken cancellationToken = default)
    {
        if (_turnExecutions is null)
        {
            return false;
        }

        var execution = await _turnExecutions.GetAsync(executionId, cancellationToken).ConfigureAwait(false);
        if (execution is null || execution.SessionId != SessionId)
        {
            return false;
        }

        if (!execution.IsOpen)
        {
            return true;
        }

        _headlessTransportDetached = true;
        var cause = NewContext(execution.SourceEventId);
        return await TryStartPendingUserBatchAsync(cause, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> HasOpenConversationExecutionsAsync(CancellationToken cancellationToken = default)
    {
        if (_turnExecutions is null)
        {
            return false;
        }

        var open = await _turnExecutions.ListOpenForSessionAsync(SessionId, cancellationToken).ConfigureAwait(false);
        return open.Count > 0;
    }

    public async Task RefreshDurableConversationProjectionAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await _store.LoadAsync(SessionId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return;
        }

        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginWork();
        if (!Enqueue(
                new DurableConversationProjectionRefreshReceived(NewContext(), snapshot, refreshed),
                urgent: true))
        {
            refreshed.TrySetResult();
            EndWork();
            return;
        }

        await refreshed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleDurableConversationProjectionRefreshAsync(
        DurableConversationProjectionRefreshReceived input,
        CancellationToken cancellationToken)
    {
        try
        {
            ResetForTransportResume(input.Snapshot);
            await PublishAsync(
                    new SessionOutput(input.Context, null, new ReadyOutput(await BuildReadyAsync(cancellationToken))),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            input.Refreshed.TrySetResult();
        }
    }

    private async Task ReconcileDurableConversationBeforeAttachAsync(CancellationToken cancellationToken)
    {
        if (_turnExecutions is null)
        {
            return;
        }

        var open = await _turnExecutions.ListOpenForSessionAsync(SessionId, cancellationToken).ConfigureAwait(false);
        if (open.Count > 0)
        {
            var active = _activeResponseId is { } responseId
                ? open.FirstOrDefault(item => item.ResponseId == responseId)
                : null;
            _boundConversationExecution = active ?? open
                .OrderBy(item => item.AcceptedAtUtc)
                .ThenBy(item => item.ExecutionId)
                .First();
            if (_activeResponseId is null)
            {
                var assistant = _snapshot.Entries.LastOrDefault(entry =>
                    entry.Role == ConversationRole.Assistant
                    && entry.ResponseId == _boundConversationExecution.ResponseId
                    && entry.Status == EntryStatus.Streaming);
                if (assistant is not null)
                {
                    _activeResponseId = _boundConversationExecution.ResponseId;
                    _activeResponseTriggerKind = TriggerKind.UserTurn;
                    _activeEntryId = assistant.EntryId;
                    _responseTerminal = false;
                    _responseLifecycle = ResponseLifecycle.Live;
                    _outputActivity = OutputActivity.AgentGenerating;
                }
            }

            return;
        }

        var durable = await _store.LoadAsync(SessionId, cancellationToken).ConfigureAwait(false);
        if (durable is null)
        {
            return;
        }

        var currentById = _snapshot.Entries.ToDictionary(entry => entry.EntryId);
        var hasNewerTerminal = durable.Entries.Any(entry =>
            entry.Role == ConversationRole.Assistant
            && entry.Status is EntryStatus.Completed or EntryStatus.Failed or EntryStatus.Interrupted
            && (!currentById.TryGetValue(entry.EntryId, out var current)
                || current.Status != entry.Status
                || !string.Equals(current.Text, entry.Text, StringComparison.Ordinal)));
        if (durable.Revision > _snapshot.Revision || hasNewerTerminal)
        {
            ResetForTransportResume(durable);
        }
    }

    private bool CanStartUserConversationBatch()
    {
        if (_deactivated || _activeResponseId is not null)
        {
            return false;
        }

        if (_snapshot.Status is SessionStatus.Attached or SessionStatus.Created)
        {
            return true;
        }

        return _headlessTransportDetached
            && _snapshot.Status is not SessionStatus.Ended and not SessionStatus.Ending;
    }

    private async Task<ConversationTurnExecution?> EnsureConversationExecutionForUserTurnAsync(
        ConversationEntry userEntry,
        CancellationToken cancellationToken,
        Guid? responseId = null)
    {
        if (_turnExecutions is null)
        {
            return null;
        }

        var sourceEventId = userEntry.SourceEventId ?? userEntry.EntryId;
        var existing = await _turnExecutions
            .GetBySourceEventAsync(SessionId, sourceEventId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        var executionId = _ids.NewId();
        var stableResponseId = responseId ?? _ids.NewId();
        var acceptedAt = _time.GetUtcNow();
        var proposed = ConversationTurnExecutionFactory.ForAcceptedUserTurn(
            executionId,
            stableResponseId,
            _snapshot,
            userEntry,
            acceptedAt);
        var created = await _turnExecutions.CreateAsync(proposed, cancellationToken).ConfigureAwait(false);
        return created.Item;
    }

    private async Task<IReadOnlyList<ConversationTurnExecution>> EnsureConversationExecutionsForUserBatchAsync(
        IReadOnlyList<ConversationEntry> users,
        CancellationToken cancellationToken)
    {
        if (_turnExecutions is null || users.Count == 0)
        {
            return [];
        }

        var executions = new List<ConversationTurnExecution>(users.Count);
        Guid? responseId = null;
        foreach (var user in users)
        {
            var sourceEventId = user.SourceEventId ?? user.EntryId;
            var existing = await _turnExecutions
                .GetBySourceEventAsync(SessionId, sourceEventId, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                responseId ??= existing.ResponseId;
                if (existing.ResponseId != responseId)
                {
                    throw AgentCoreErrors.Conflict("Queued conversation executions have inconsistent response identity.");
                }

                executions.Add(existing);
                continue;
            }

            var created = await EnsureConversationExecutionForUserTurnAsync(
                    user,
                    cancellationToken,
                    responseId)
                .ConfigureAwait(false)
                ?? throw AgentCoreErrors.Persistence("Failed to create conversation execution ownership.");
            responseId ??= created.ResponseId;
            executions.Add(created);
        }

        return executions;
    }

    private async Task<bool> BindConversationExecutionsForStartAsync(
        IReadOnlyList<ConversationTurnExecution> executions,
        CancellationToken cancellationToken)
    {
        if (executions.Count == 0)
        {
            return _turnExecutions is null;
        }

        var bound = new List<ConversationTurnExecution>(executions.Count);
        foreach (var execution in executions)
        {
            if (execution.Status == ConversationTurnExecutionStatus.Running && execution.Claim is not null)
            {
                bound.Add(execution);
                continue;
            }

            if (execution.Status != ConversationTurnExecutionStatus.Queued || _turnExecutions is null)
            {
                return false;
            }

            var now = _time.GetUtcNow();
            var claimed = await _turnExecutions.TryClaimAsync(
                    execution.ExecutionId,
                    _ids.NewId(),
                    now,
                    now.AddMinutes(5),
                    cancellationToken)
                .ConfigureAwait(false);
            if (claimed is null)
            {
                return false;
            }

            bound.Add(claimed);
        }

        var responseId = bound[0].ResponseId;
        if (bound.Any(item => item.ResponseId != responseId))
        {
            throw AgentCoreErrors.Conflict("Conversation execution batch has inconsistent response identity.");
        }

        _boundConversationExecution = bound[0];
        return true;
    }

    private void MarkConversationExecutionPendingTerminal()
    {
        if (_boundConversationExecution is null)
        {
            return;
        }

        _pendingTerminalExecutionId = _boundConversationExecution.ExecutionId;
        _pendingTerminalResponseId = _boundConversationExecution.ResponseId;
    }

    private async Task CloseBoundExecutionIfAssistantTerminalAsync(CancellationToken cancellationToken)
    {
        if (_boundConversationExecution is null)
        {
            return;
        }

        var assistant = _snapshot.Entries.LastOrDefault(entry =>
            entry.Role == ConversationRole.Assistant
            && entry.ResponseId == _boundConversationExecution.ResponseId);
        if (assistant is not { Status: EntryStatus.Interrupted or EntryStatus.Failed or EntryStatus.Completed })
        {
            return;
        }

        _pendingTerminalExecutionId = _boundConversationExecution.ExecutionId;
        _pendingTerminalResponseId = _boundConversationExecution.ResponseId;
        await FinalizeConversationExecutionAfterPersistAsync(_snapshot, cancellationToken).ConfigureAwait(false);
    }

    internal async Task MarkBoundExecutionWaitingForApprovalAsync(CancellationToken cancellationToken)
    {
        if (_turnExecutions is null
            || _boundConversationExecution is not { Claim.Generation: var generation } bound)
        {
            return;
        }

        var open = await _turnExecutions.ListOpenForSessionAsync(SessionId, cancellationToken).ConfigureAwait(false);
        foreach (var execution in open.Where(item => item.ResponseId == bound.ResponseId))
        {
            var claimGeneration = execution.Claim?.Generation ?? generation;
            var updated = await _turnExecutions.MarkWaitingForApprovalAsync(
                    execution.ExecutionId,
                    execution.Revision,
                    claimGeneration,
                    _time.GetUtcNow(),
                    cancellationToken)
                .ConfigureAwait(false);
            if (updated.ExecutionId == bound.ExecutionId)
            {
                _boundConversationExecution = updated;
            }
        }
    }

    internal async Task ResumeBoundExecutionAfterApprovalAsync(CancellationToken cancellationToken)
    {
        if (_turnExecutions is null
            || _boundConversationExecution is not { Status: ConversationTurnExecutionStatus.WaitingForApproval, Claim.Generation: var generation } bound)
        {
            return;
        }

        var open = await _turnExecutions.ListOpenForSessionAsync(SessionId, cancellationToken).ConfigureAwait(false);
        foreach (var execution in open.Where(item => item.ResponseId == bound.ResponseId))
        {
            var claimGeneration = execution.Claim?.Generation ?? generation;
            var updated = await _turnExecutions.ResumeRunningAsync(
                    execution.ExecutionId,
                    execution.Revision,
                    claimGeneration,
                    _time.GetUtcNow(),
                    cancellationToken)
                .ConfigureAwait(false);
            if (updated.ExecutionId == bound.ExecutionId)
            {
                _boundConversationExecution = updated;
            }
        }
    }

    private async Task FinalizeConversationExecutionAfterPersistAsync(
        SessionSnapshot saved,
        CancellationToken cancellationToken)
    {
        if (_turnExecutions is null || _pendingTerminalExecutionId is not Guid executionId)
        {
            return;
        }

        try
        {
            var primary = await _turnExecutions.GetAsync(executionId, cancellationToken).ConfigureAwait(false);
            if (primary is null)
            {
                return;
            }

            var now = _time.GetUtcNow();
            var open = await _turnExecutions.ListOpenForSessionAsync(SessionId, cancellationToken).ConfigureAwait(false);
            var assistant = saved.Entries.LastOrDefault(entry =>
                entry.Role == ConversationRole.Assistant && entry.ResponseId == primary.ResponseId);

            if (assistant is not { Status: EntryStatus.Completed or EntryStatus.Failed or EntryStatus.Interrupted })
            {
                throw AgentCoreErrors.Persistence("Terminal assistant entry is not durable.");
            }

            foreach (var execution in open.Where(item => item.ResponseId == primary.ResponseId))
            {
                var generation = execution.Claim?.Generation
                    ?? throw new InvalidOperationException("Open conversational execution is missing its claim.");
                var working = execution.AssistantEntryId == assistant.EntryId
                    ? execution
                    : await _turnExecutions
                        .AttachAssistantAsync(
                            execution.ExecutionId,
                            execution.Revision,
                            generation,
                            assistant.EntryId,
                            now,
                            cancellationToken)
                        .ConfigureAwait(false);
                if (assistant.Status == EntryStatus.Interrupted)
                {
                    await _turnExecutions
                        .CommitCancellationAsync(
                            working.ExecutionId,
                            working.Revision,
                            generation,
                            now,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                else if (assistant.Status == EntryStatus.Failed)
                {
                    await _turnExecutions
                        .FailAsync(
                            working.ExecutionId,
                            working.Revision,
                            generation,
                            now,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await _turnExecutions
                        .CompleteAsync(
                            working.ExecutionId,
                            working.Revision,
                            generation,
                            now,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (Exception)
        {
            var failedAt = _time.GetUtcNow();
            try
            {
                var open = await _turnExecutions.ListOpenForSessionAsync(SessionId, cancellationToken).ConfigureAwait(false);
                foreach (var current in open.Where(item =>
                             _pendingTerminalResponseId is null || item.ResponseId == _pendingTerminalResponseId))
                {
                    if (current.Claim?.Generation is not { } generation)
                    {
                        continue;
                    }

                    await _turnExecutions
                        .FailAsync(current.ExecutionId, current.Revision, generation, failedAt, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
            }
        }
        finally
        {
            _boundConversationExecution = null;
            ClearConversationExecutionTerminalPending();
        }
    }

    private void ClearConversationExecutionTerminalPending()
    {
        _pendingTerminalExecutionId = null;
        _pendingTerminalResponseId = null;
    }
}
