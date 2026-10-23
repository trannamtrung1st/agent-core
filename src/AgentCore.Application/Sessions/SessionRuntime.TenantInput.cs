using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime
{
    private long _tenantInputSequenceFloor;
    private TaskCompletionSource<TenantHumanInputCommit?>? _tenantInterruptionReply;
    // Existing model/brain producers await their Processed acknowledgement, so holding
    // these normalized outputs applies bounded backpressure without buffering tokens.
    private readonly Queue<SessionInput> _tenantInterruptionOutputs = new();
    private TaskCompletionSource _tenantInterruptionOutputObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task WaitForTenantInterruptionOutputHeldAsync(CancellationToken ct) => Volatile.Read(ref _tenantInterruptionOutputObserved).Task.WaitAsync(ct);
    private sealed record TenantInputReceived(EventContext Context, TenantHumanInput Input,
        TaskCompletionSource<TenantHumanInputCommit?> Completed) : SessionInput(Context);
    public async Task<TenantHumanInputCommit?> QueueTenantInputAsync(TenantHumanInput input, CancellationToken ct = default)
    {
        var completed = new TaskCompletionSource<TenantHumanInputCommit?>(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginWork();
        if (!Enqueue(new TenantInputReceived(NewContext(), input, completed), urgent: true)) return null;
        return await completed.Task.WaitAsync(ct).ConfigureAwait(false);
    }
    private void HandleTenantInput(TenantInputReceived message)
    {
        try
        {
            var input = message.Input; var owner = input.Owner;
            if (_runtimeOwner is not { } runtime || runtime.TenantId != owner.TenantId || runtime.AgentInstanceId != owner.AgentInstanceId
                || runtime.ExecutorPrincipalId != owner.ExecutorPrincipalId || SessionId != owner.SessionId || _store is not ITenantRuntimeInputWriter)
                throw new AccessDeniedException("tenant_context_mismatch");
            if (_tenantTerminalCommitted || _tenantApprovalPending || _tenantApprovalSuspended || _tenantWaitSuspended || _responseTerminal)
            { message.Completed.TrySetResult(null); return; }
            TenantHumanInputRules.Eligible(_snapshot);
            if (input.Admission.Interruption is not null && _tenantInterruptionReply is not null) throw new SecurityConflictException("session_in_use");
            if (_activeEntryId is not null) UpdateStreamingAssistant();
            // Reserve only an opaque sequence, not optimistic content. Later mailbox events cannot
            // collide with this slot while its authority-checked write is pending. Refusal leaves a gap.
            var sequence = NextSequence(); _tenantInputSequenceFloor = sequence;
            input = input with { Admission = input.Admission with { Input = input.Admission.Input with { Sequence = sequence } } };
            if (input.Admission.Interruption is not null)
            {
                _tenantInterruptionReply = message.Completed;
                Volatile.Write(ref _tenantInterruptionOutputObserved, new(TaskCreationOptions.RunContinuationsAsynchronously));
            }
            RequestPersist(_snapshot, tenantInput: input, tenantInputCompleted: message.Completed);
        }
        catch (Exception ex)
        {
            CompleteTenantInputInterruption(message.Completed);
            message.Completed.TrySetException(ex);
        }
    }

    private bool HoldTenantInterruptionOutput(SessionInput input)
    {
        if (_tenantInterruptionReply is null || input.Context.Epoch != _epoch) return false;
        var response = input switch { ModelResultReceived model => model.ResponseId, BrainReturned brain => brain.ResponseId, BrainFailed failed => failed.ResponseId, _ => (Guid?)null };
        if (response is null || response != (_activeResponseId ?? _boundAgentRun?.ResponseId)) return false;
        _tenantInterruptionOutputs.Enqueue(input); _tenantInterruptionOutputObserved.TrySetResult(); return true;
    }
    private void CompleteTenantInputInterruption(TaskCompletionSource<TenantHumanInputCommit?>? reply)
    {
        if (reply is null || !ReferenceEquals(_tenantInterruptionReply, reply)) return;
        _tenantInterruptionReply = null;
        if (_tenantTerminalCommitted) DiscardTenantInterruptionOutputs();
    }
    private async Task DrainTenantInterruptionOutputsAsync(CancellationToken ct)
    {
        while (_tenantInterruptionReply is null && _tenantInterruptionOutputs.TryDequeue(out var held))
            await DispatchAsync(held, ct).ConfigureAwait(false);
    }
    private void DiscardTenantInterruptionOutputs()
    {
        _tenantInterruptionReply = null;
        while (_tenantInterruptionOutputs.TryDequeue(out var held)) { CompleteInputWaiters(held, false); EndWork(); }
    }

    private async Task ApplyTenantInputInterruptionAsync(EventContext context, AgentRun interrupted, CancellationToken ct)
    {
        // The write lane has atomically admitted the new root and stopped this exact Run.
        // Tombstone output before cancelling any provider or allowing the next dispatcher.
        _tenantTerminalCommitted = true;
        _responseLifecycle = ResponseLifecycle.Superseded;
        _allowQueuedSuffixAutoDispatch = false;
        _boundAgentRun = interrupted;
        ClearAgentRunTerminalPending();
        try
        {
            if (_activeResponseId is { } response)
                await TerminalizeActiveResponseAsync(context, response, ct, "userSteer", requestPersist: false).ConfigureAwait(false);
            else
            {
                _responseTerminal = true;
                _responseCts?.Cancel(); _brainEvaluationCts?.Cancel(); _ttsCts?.Cancel();
            }
        }
        finally { _boundAgentRun = null; }
    }
}
