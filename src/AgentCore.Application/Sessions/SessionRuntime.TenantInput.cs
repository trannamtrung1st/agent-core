using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime
{
    private long _tenantInputSequenceFloor;
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
            if (_tenantCancellationCommitted || _tenantApprovalPending || _tenantApprovalSuspended || _responseTerminal)
            { message.Completed.TrySetResult(null); return; }
            TenantHumanInputRules.Eligible(_snapshot);
            if (_activeEntryId is not null) UpdateStreamingAssistant();
            // Reserve only an opaque sequence, not optimistic content. Later mailbox events cannot
            // collide with this slot while its authority-checked write is pending. Refusal leaves a gap.
            var sequence = NextSequence(); _tenantInputSequenceFloor = sequence;
            input = input with { Admission = input.Admission with { Input = input.Admission.Input with { Sequence = sequence } } };
            RequestPersist(_snapshot, tenantInput: input, tenantInputCompleted: message.Completed);
        }
        catch (Exception ex) { message.Completed.TrySetException(ex); }
    }
}
