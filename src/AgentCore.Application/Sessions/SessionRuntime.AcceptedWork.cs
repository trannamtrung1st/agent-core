using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime
{
    private bool _headlessTransportDetached;
    private TriggerKind? _activeResponseTriggerKind;

    public bool HeadlessTransportDetached => _headlessTransportDetached;

    public async Task<bool> HasAcceptedConversationWorkAsync(CancellationToken cancellationToken = default)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = NewContext();
        BeginWork();
        if (!Enqueue(new AcceptedConversationWorkQueryReceived(context, result), urgent: true))
        {
            return false;
        }

        return await result.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task WaitUntilAcceptedConversationWorkSettledAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await HasAcceptedConversationWorkAsync(cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task TransportDetachAsync(CancellationToken cancellationToken = default) =>
        DetachInternalAsync(DetachPhase.TransportOnly, cancellationToken);

    public Task FinalizeDetachedPauseAsync(CancellationToken cancellationToken = default) =>
        DetachInternalAsync(DetachPhase.FinalizePaused, cancellationToken);

    private async Task DetachInternalAsync(DetachPhase phase, CancellationToken cancellationToken)
    {
        var detached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = NewContext();
        BeginWork();
        if (!Enqueue(new DetachReceived(context, phase, detached), urgent: true))
        {
            return;
        }

        await detached.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private bool HasAcceptedConversationWork()
    {
        if (_pendingApproval is not null)
        {
            return true;
        }

        if (HasDurablePendingUserBatch())
        {
            return true;
        }

        if (_brainEvaluationCts is not null && !_proactiveBrainInFlight)
        {
            return true;
        }

        if (_activeResponseId is { } responseId && _activeResponseTriggerKind == TriggerKind.UserTurn)
        {
            return IsUserResponseStillExecuting(responseId);
        }

        if (_outputActivity == OutputActivity.ProcessingAttachments && HasDurablePendingUserBatch())
        {
            return true;
        }

        return false;
    }

    private bool IsUserResponseStillExecuting(Guid responseId)
    {
        var assistant = _snapshot.Entries.LastOrDefault(
            entry => entry.Role == ConversationRole.Assistant && entry.ResponseId == responseId);
        return assistant is null || assistant.Status == EntryStatus.Streaming;
    }

    private bool HasDurablePendingUserBatch()
    {
        var suffix = TrailingUserSuffix.Of(_snapshot.Entries);
        return suffix.Count > 0 && suffix.All(entry => !_undurableUserEntryIds.Contains(entry.EntryId));
    }

    private void HandleAcceptedConversationWorkQuery(AcceptedConversationWorkQueryReceived input) =>
        input.Result.TrySetResult(HasAcceptedConversationWork());

    private bool ShouldBlockProactiveWhileHeadless(TriggerKind kind) =>
        _headlessTransportDetached && kind != TriggerKind.UserTurn;
}
