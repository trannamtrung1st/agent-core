using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime
{
    private sealed record TenantForegroundReceived(EventContext Context, TenantSessionForeground Request,
        TaskCompletionSource<bool> Completed) : SessionInput(Context);

    public async Task<bool> ContinueTenantInChatAsync(TenantSessionForeground request, CancellationToken ct = default)
    {
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginWork();
        if (!Enqueue(new TenantForegroundReceived(NewContext(), request, completed), urgent: true)) return false;
        return await completed.Task.WaitAsync(ct).ConfigureAwait(false);
    }
    private void HandleTenantForeground(TenantForegroundReceived input)
    {
        try
        {
            var owner = input.Request.Owner;
            if (_runtimeOwner is not { } runtime || runtime.TenantId != owner.TenantId || runtime.AgentInstanceId != owner.AgentInstanceId
                || runtime.ExecutorPrincipalId != owner.ExecutorPrincipalId || SessionId != owner.SessionId
                || _store is not ITenantRuntimeForegroundWriter) throw new AccessDeniedException("tenant_context_mismatch");
            if (_tenantCancellationCommitted || _tenantApprovalPending || _tenantApprovalSuspended)
            { input.Completed.TrySetResult(false); return; }
            TenantSessionForegroundRules.Eligible(_snapshot);
            // The serialized writer rechecks the original human and current IAM before adopting visibility.
            // No optimistic surface change may leak into an ordinary snapshot after a failed command.
            RequestPersist(_snapshot, ended: input.Completed, tenantForeground: input.Request);
        }
        catch (Exception ex) { input.Completed.TrySetException(ex); }
    }
}
