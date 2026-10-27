using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
namespace AgentCore.Application.Sessions;
public sealed partial class SessionRuntime
{
    private sealed record GoalFocusReceived(EventContext Context, GoalFocus Request, TaskCompletionSource<GoalFocusResult?> Completed) : SessionInput(Context);
    public async Task<GoalFocusResult?> FocusGoalAsync(GoalFocus request, CancellationToken ct = default)
    {
        var completed = new TaskCompletionSource<GoalFocusResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginWork(); if (!Enqueue(new GoalFocusReceived(NewContext(), request, completed), urgent: true)) return null;
        return await completed.Task.WaitAsync(ct).ConfigureAwait(false);
    }
    private void HandleGoalFocus(GoalFocusReceived input)
    {
        try
        {
            if (_runtimeOwner?.TenantId != input.Request.Owner.TenantId || SessionId != input.Request.Owner.SessionId || _store is not ITenantRuntimeGoalWriter) throw new AccessDeniedException("tenant_context_mismatch");
            if (_tenantTerminalCommitted || _tenantApprovalPending || _tenantApprovalSuspended || _boundAgentRun is null) { input.Completed.TrySetResult(null); return; }
            RequestPersist(_snapshot with { FocusedGoalId = input.Request.GoalId, GoalFocusRevision = _snapshot.GoalFocusRevision + 1, GoalFocusRequestId = input.Request.RequestId }, goalFocus: input.Request, goalFocusCompleted: input.Completed);
        }
        catch (Exception ex) { input.Completed.TrySetException(ex); }
    }
}
