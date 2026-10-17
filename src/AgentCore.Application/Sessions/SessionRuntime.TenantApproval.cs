using System.Text.Json;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Observability;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime
{
    private bool _tenantApprovalPending;
    private bool _tenantApprovalSuspended;
    private Task? _tenantApprovalPreparationTask;
    private TenantApprovalRequested? _tenantApprovalRequest;
    private sealed record TenantApprovalToolResult(bool Suspended, string Text);
    private sealed record TenantApprovalRequested(EventContext Context, Guid ResponseId, ModelToolCall Call,
        TaskCompletionSource<TenantApprovalToolResult> Completed) : SessionInput(Context)
    { public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); }
    private sealed record TenantApprovalBarrierFailed(EventContext Context, TenantApprovalRequested Request) : SessionInput(Context);
    private sealed record TenantApprovalPrepared(EventContext Context, TenantApprovalRequested Request, AgentRun? Run, bool Unavailable, string Text) : SessionInput(Context);

    private async Task<TenantApprovalToolResult> RequestTenantApprovalAsync(EventContext cause, Guid responseId, ModelToolCall call, CancellationToken ct)
    {
        var completed = new TaskCompletionSource<TenantApprovalToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginWork();
        if (!Enqueue(new TenantApprovalRequested(WorkerContext(cause), responseId, call, completed), urgent: true))
            return new(false, "{\"error\":\"unavailable\"}");
        return await completed.Task.WaitAsync(ct).ConfigureAwait(false);
    }
    private async Task HandleTenantApprovalAsync(TenantApprovalRequested input, CancellationToken ct)
    {
        if (_runtimeOwner is null || _tenantApprovalPending || _tenantApprovalSuspended || _activeResponseId != input.ResponseId
            || !await OwnsWorkerAsync(input.Context, input.ResponseId, ct).ConfigureAwait(false))
        { input.Completed.TrySetResult(new(false, "{\"error\":\"forbidden\"}")); return; }
        try { _ = TenantApprovalToolContract.Parse(input.Call); }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        { input.Completed.TrySetResult(new(false, "{\"error\":\"invalid\",\"reasonCode\":\"invalid_arguments\"}")); return; }
        _tenantApprovalPending = true;
        _tenantApprovalRequest = input;
        var saved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try { RequestPersist(_snapshot, PersistKind.Checkpoint, then: token => LaunchTenantApprovalPreparationAsync(input, token), ended: saved, tenantApprovalBarrier: true); }
        catch (Exception ex)
        {
            _tenantApprovalPending = false; _tenantApprovalRequest = null;
            DiagnosticLog.Warning(_logger, ex, _diagnostics.NewId(), "Tenant approval persistence barrier unavailable.", new(AgentRunId: _boundAgentRun?.AgentRunId));
            input.Completed.TrySetResult(new(false, "{\"error\":\"persistence_unavailable\"}")); return;
        }
        _ = ObserveTenantApprovalBarrierAsync(input, saved.Task);
    }
    private async Task ObserveTenantApprovalBarrierAsync(TenantApprovalRequested input, Task<bool> saved)
    {
        await saved.ConfigureAwait(false);
        if (input.Completed.Task.IsCompleted || input.Started.Task.IsCompleted) return;
        BeginWork(); Enqueue(new TenantApprovalBarrierFailed(WorkerContext(input.Context), input), urgent: true);
    }
    private void FailTenantApprovalBarrier(TenantApprovalBarrierFailed input)
    {
        if (!ReferenceEquals(_tenantApprovalRequest, input.Request)) return;
        _tenantApprovalPending = false; _tenantApprovalRequest = null;
        input.Request.Completed.TrySetResult(new(false, "{\"error\":\"persistence_unavailable\"}"));
    }
    private async Task LaunchTenantApprovalPreparationAsync(TenantApprovalRequested input, CancellationToken ct)
    {
        if (_activeResponseId != input.ResponseId || !await OwnsWorkerAsync(input.Context, input.ResponseId, ct).ConfigureAwait(false)) return;
        var run = _boundAgentRun!;
        var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId: _snapshot.AgentInstanceId,
            AgentRunId: run.AgentRunId, OwnedSessionId: SessionId, TenantId: _runtimeOwner!.Value.TenantId, AgentRunGeneration: input.Context.AgentRunGeneration);
        var sessionId = SessionId; var lifetime = _lifetime.Token;
        input.Started.TrySetResult();
        _tenantApprovalPreparationTask = Task.Run(() => PrepareTenantApprovalWorkerAsync(input, run.Owner, run.AgentRunId, sessionId, admission, lifetime));
    }
    private async Task PrepareTenantApprovalWorkerAsync(TenantApprovalRequested input, AgentRunOwner owner, Guid runId, Guid sessionId, ToolExecutionAdmission admission, CancellationToken ct)
    {
        var text = "{\"error\":\"unavailable\"}";
        try
        {
            var prepared = await _tools.RequestTenantAuthorizationAsync(sessionId, input.Call, admission, ct).ConfigureAwait(false);
            text = JsonSerializer.Serialize(new { decision = JsonNamingPolicy.CamelCase.ConvertName(prepared.Decision.ToString()), reasonCode = prepared.ReasonCode,
                requestId = prepared.Request?.RequestId, applied = false });
        }
        catch (AccessDeniedException ex) { text = JsonSerializer.Serialize(new { error = "forbidden", reasonCode = ex.ReasonCode }); }
        catch (SecurityConflictException ex) { text = JsonSerializer.Serialize(new { error = "conflict", reasonCode = ex.ReasonCode }); }
        catch (OperationCanceledException) { text = "{\"error\":\"cancelled\"}"; }
        catch (ArgumentException) { text = "{\"error\":\"invalid\",\"reasonCode\":\"invalid_arguments\"}"; }
        catch (Exception ex) { DiagnosticLog.Warning(_logger, ex, _diagnostics.NewId(), "Tenant approval preparation failed.", new(AgentRunId: runId)); }
        finally
        {
            // A cancellation can race a successful durable commit. The saved Run decides whether resources must be released.
            AgentRun? run = null; var unavailable = false;
            try { run = await _agentRuns.GetAsync(owner, runId, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex)
            {
                unavailable = true;
                DiagnosticLog.Warning(_logger, ex, _diagnostics.NewId(), "Tenant approval commit status unavailable.", new(AgentRunId: runId));
            }
            BeginWork();
            if (!Enqueue(new TenantApprovalPrepared(WorkerContext(input.Context), input, run, unavailable, text), urgent: true))
                input.Completed.TrySetResult(new(true, text));
        }
    }
    private async Task ApplyTenantApprovalPreparedAsync(TenantApprovalPrepared input, CancellationToken ct)
    {
        if (!ReferenceEquals(_tenantApprovalRequest, input.Request))
        { input.Request.Completed.TrySetResult(new(true, "{\"error\":\"superseded\"}")); return; }
        _tenantApprovalPending = false; _tenantApprovalRequest = null;
        var run = input.Run;
        if (input.Unavailable || run is null || run.Status != AgentRunStatus.Running || run.CancellationRequested
            || run.Claim?.Generation != input.Context.AgentRunGeneration || input.Context.Epoch != _epoch || _activeResponseId != input.Request.ResponseId)
        {
            if (run is not null) _boundAgentRun = run;
            _tenantApprovalSuspended = true;
            _responseTerminal = true; _activeResponseId = null; _activeEntryId = null; _outputActivity = OutputActivity.Idle;
            input.Request.Completed.TrySetResult(new(true, input.Text));
            _responseCts?.Cancel();
            if (run is { Status: AgentRunStatus.WaitingForApproval, ApprovalRequestId: { } request })
                await _output.PublishAsync(new(WorkerContext(input.Context), input.Request.ResponseId, new DurableApprovalPendingOutput(request, run.AgentRunId)), ct).ConfigureAwait(false);
        }
        else input.Request.Completed.TrySetResult(new(false, input.Text));
    }
}
