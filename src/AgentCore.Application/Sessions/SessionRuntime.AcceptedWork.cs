using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime
{
    private bool _headlessTransportDetached;
    private TriggerKind? _activeResponseTriggerKind;
    private int _userConversationTerminalPersistDepth;

    public bool HeadlessTransportDetached => _headlessTransportDetached;

    public Task<bool> HasAcceptedConversationWorkAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return Task.FromResult(HasAcceptedConversationWork());
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
        if (_boundConversationExecution?.IsOpen == true
            && (!_responseTerminal || HasPendingConversationTerminalPersist()))
        {
            return true;
        }

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

        if (_activeResponseId is not null
            && !_responseTerminal
            && _activeResponseTriggerKind == TriggerKind.UserTurn)
        {
            return true;
        }

        if (HasPendingConversationTerminalPersist())
        {
            return true;
        }

        if (_outputActivity == OutputActivity.ProcessingAttachments && HasDurablePendingUserBatch())
        {
            return true;
        }

        return false;
    }

    private bool HasPendingConversationTerminalPersist() => _userConversationTerminalPersistDepth > 0;

    private void BeginUserConversationTerminalPersist() =>
        Interlocked.Increment(ref _userConversationTerminalPersistDepth);

    private void EndUserConversationTerminalPersist()
    {
        if (Interlocked.Decrement(ref _userConversationTerminalPersistDepth) < 0)
        {
            Interlocked.Exchange(ref _userConversationTerminalPersistDepth, 0);
        }
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
