using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime
{
    private sealed record TenantContextReceived(EventContext Context, TenantContextSwitch Request,
        TaskCompletionSource<TenantContextSwitchResult?> Completed) : SessionInput(Context);
    public async Task<TenantContextSwitchResult?> SwitchTenantContextAsync(TenantContextSwitch request, CancellationToken ct = default)
    {
        var completed = new TaskCompletionSource<TenantContextSwitchResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginWork(); if (!Enqueue(new TenantContextReceived(NewContext(), request, completed), urgent: true)) return null;
        return await completed.Task.WaitAsync(ct).ConfigureAwait(false);
    }
    private void HandleTenantContext(TenantContextReceived input)
    {
        try
        {
            var owner = input.Request.Owner;
            if (_runtimeOwner is not { } runtime || runtime.TenantId != owner.TenantId || runtime.AgentInstanceId != owner.AgentInstanceId
                || runtime.ExecutorPrincipalId != owner.ExecutorPrincipalId || SessionId != owner.SessionId || _store is not ITenantRuntimeContextWriter)
                throw new AccessDeniedException("tenant_context_mismatch");
            if (_tenantTerminalCommitted || _tenantApprovalPending || _tenantApprovalSuspended || _boundAgentRun is null)
            { input.Completed.TrySetResult(null); return; }
            var current = _snapshot.ContextSelection ?? throw new AccessDeniedException("space_context_required");
            var target = TenantSessionContextRules.Selection(input.Request, current, _boundAgentRun.AgentRunId, _time.GetUtcNow());
            // The current Run keeps its immutable context. Only the serialized authoritative writer
            // can publish this next-turn selection; failed commands never enter ordinary checkpoints.
            RequestPersist(_snapshot with { ContextSelection = target }, tenantContext: input.Request, tenantContextCompleted: input.Completed);
        }
        catch (Exception ex) { input.Completed.TrySetException(ex); }
    }
}
