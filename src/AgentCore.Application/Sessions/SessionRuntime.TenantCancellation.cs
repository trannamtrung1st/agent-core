using AgentCore.Application.Events;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime
{
    private bool _tenantCancellationCommitted;
    private sealed record TenantRunCancellationReceived(EventContext Context, TenantRunCancellation Request,
        TenantRunCancellationService Service, TaskCompletionSource<AgentRun> Completed) : SessionInput(Context);

    public async Task<AgentRun> CancelTenantRunAsync(TenantRunCancellation request, TenantRunCancellationService service, CancellationToken ct = default)
    {
        var completed = new TaskCompletionSource<AgentRun>(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginWork();
        if (!Enqueue(new TenantRunCancellationReceived(NewContext(), request, service, completed), urgent: true))
            throw new AccessDeniedException("tenant_runtime_unavailable");
        return await completed.Task.WaitAsync(ct).ConfigureAwait(false);
    }
    private async Task HandleTenantRunCancellationAsync(TenantRunCancellationReceived input, CancellationToken ct)
    {
        AgentRun saved;
        try
        {
            var request = input.Request;
            if (_runtimeOwner != request.Owner || request.SessionId != SessionId
                || _boundAgentRun is { } bound && (bound.AgentRunId != request.RunId || bound.Claim?.Generation != request.Generation))
                throw new AccessDeniedException("tenant_context_mismatch");
            saved = await input.Service.CommitAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) { input.Completed.TrySetException(ex); return; }

        // The owning mailbox issued the atomic terminal write. Supersede before cancelling any worker.
        _tenantCancellationCommitted = true;
        _responseLifecycle = ResponseLifecycle.Superseded;
        _allowQueuedSuffixAutoDispatch = false;
        _boundAgentRun = saved;
        ClearAgentRunTerminalPending();
        try
        {
            if (_activeResponseId is { } response)
                await TerminalizeActiveResponseAsync(input.Context, response, ct, "userStop", requestPersist: false).ConfigureAwait(false);
            else
            {
                _responseTerminal = true;
                _responseCts?.Cancel(); _brainEvaluationCts?.Cancel(); _ttsCts?.Cancel();
                ClearPendingApproval();
            }
        }
        catch (Exception ex) { DiagnosticLog.Warning(_logger, ex, _ids.NewId(), "Tenant cancellation cleanup did not settle."); }
        finally { _boundAgentRun = null; input.Completed.TrySetResult(saved); }
    }
}
