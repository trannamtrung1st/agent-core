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
        return await TryStartPendingUserBatchAsync(cause, cancellationToken, execution).ConfigureAwait(false);
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
        EventContext cause,
        CancellationToken cancellationToken)
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
        var responseId = _ids.NewId();
        var acceptedAt = _time.GetUtcNow();
        var proposed = ConversationTurnExecutionFactory.ForAcceptedUserTurn(
            executionId,
            responseId,
            _snapshot,
            userEntry,
            acceptedAt);
        var created = await _turnExecutions.CreateAsync(proposed, cancellationToken).ConfigureAwait(false);
        return created.Item;
    }

    private async Task<bool> BindConversationExecutionForStartAsync(
        ConversationTurnExecution execution,
        CancellationToken cancellationToken)
    {
        if (_turnExecutions is null)
        {
            _boundConversationExecution = execution;
            return true;
        }

        if (execution.Status == ConversationTurnExecutionStatus.Running && execution.Claim is not null)
        {
            _boundConversationExecution = execution;
            return true;
        }

        if (execution.Status != ConversationTurnExecutionStatus.Queued)
        {
            return false;
        }

        var now = _time.GetUtcNow();
        var generation = _ids.NewId();
        var claimed = await _turnExecutions.TryClaimAsync(
                execution.ExecutionId,
                generation,
                now,
                now.AddMinutes(5),
                cancellationToken)
            .ConfigureAwait(false);
        if (claimed is null)
        {
            return false;
        }

        _boundConversationExecution = claimed;
        return true;
    }

    private void MarkConversationExecutionPendingTerminal()
    {
        if (_boundConversationExecution is null)
        {
            return;
        }

        _pendingTerminalExecutionId = _boundConversationExecution.ExecutionId;
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
        await FinalizeConversationExecutionAfterPersistAsync(_snapshot, cancellationToken).ConfigureAwait(false);
    }

    internal async Task MarkBoundExecutionWaitingForApprovalAsync(CancellationToken cancellationToken)
    {
        if (_turnExecutions is null
            || _boundConversationExecution is not { Claim.Generation: var generation } bound)
        {
            return;
        }

        var updated = await _turnExecutions.MarkWaitingForApprovalAsync(
                bound.ExecutionId,
                bound.Revision,
                generation,
                _time.GetUtcNow(),
                cancellationToken)
            .ConfigureAwait(false);
        _boundConversationExecution = updated;
    }

    internal async Task ResumeBoundExecutionAfterApprovalAsync(CancellationToken cancellationToken)
    {
        if (_turnExecutions is null
            || _boundConversationExecution is not { Status: ConversationTurnExecutionStatus.WaitingForApproval, Claim.Generation: var generation } bound)
        {
            return;
        }

        var updated = await _turnExecutions.ResumeRunningAsync(
                bound.ExecutionId,
                bound.Revision,
                generation,
                _time.GetUtcNow(),
                cancellationToken)
            .ConfigureAwait(false);
        _boundConversationExecution = updated;
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
            var execution = await _turnExecutions.GetAsync(executionId, cancellationToken).ConfigureAwait(false);
            if (execution is null)
            {
                return;
            }

            var revision = execution.Revision;
            var generation = execution.Claim?.Generation
                ?? throw new InvalidOperationException("Open conversational execution is missing its claim.");

            var assistant = saved.Entries.LastOrDefault(entry => entry.Role == ConversationRole.Assistant);
            if (assistant is null)
            {
                await _turnExecutions
                    .FailAsync(executionId, revision, generation, _time.GetUtcNow(), cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var now = _time.GetUtcNow();
            var working = execution.AssistantEntryId == assistant.EntryId
                ? execution
                : await _turnExecutions
                    .AttachAssistantAsync(executionId, revision, generation, assistant.EntryId, now, cancellationToken)
                    .ConfigureAwait(false);
            var claimGeneration = working.Claim?.Generation ?? generation;
            if (assistant.Status == EntryStatus.Interrupted)
            {
                await _turnExecutions
                    .CommitCancellationAsync(executionId, working.Revision, claimGeneration, now, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (assistant.Status == EntryStatus.Failed)
            {
                await _turnExecutions
                    .FailAsync(executionId, working.Revision, claimGeneration, now, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await _turnExecutions
                    .CompleteAsync(executionId, working.Revision, claimGeneration, now, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            var failedAt = _time.GetUtcNow();
            try
            {
                var current = await _turnExecutions.GetAsync(executionId, cancellationToken).ConfigureAwait(false);
                if (current is { IsOpen: true, Claim.Generation: { } generation })
                {
                    await _turnExecutions
                        .FailAsync(executionId, current.Revision, generation, failedAt, cancellationToken)
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

    private void ClearConversationExecutionTerminalPending() => _pendingTerminalExecutionId = null;
}
