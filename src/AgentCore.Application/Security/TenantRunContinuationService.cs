using AgentCore.Application.Ports;
using AgentCore.Application.Execution;
using AgentCore.Application.Tools;
using System.Text.Json;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

// Model-visible result data, never an authorization token or another operation's arguments.
public sealed record TenantApprovalOutcome(Guid TenantId, Guid RunId, Guid RequestId, Guid OperationId,
    Guid InstanceId, Guid RootActivationId, TenantApprovalStatus ApprovalStatus, TenantOperationOutcome Outcome,
    string ReasonCode, long? InstanceRevision, long? PersonaRevision);

public static class TenantRunContinuationRules
{
    public static void Require(AgentRun run, TenantApprovalRequest? request, AgentRun? source = null)
    {
        var context = run.Admission.ExecutionContext;
        if (request is null || context is null || !Enum.IsDefined(request.Status) || request.Status == TenantApprovalStatus.Pending
            || request.RequestId != run.ApprovalRequestId || request.SessionId != run.SessionId
            || request.TenantId != context.TenantId || request.ExecutorPrincipalId != context.AgentPrincipalId
            || request.OriginalInitiatorPrincipalId != context.OriginalInitiatorPrincipalId || request.RootActivationId != context.RootActivationId
            || request.Operation.InstanceId != run.AgentInstanceId)
            throw new SecurityConflictException("approval_continuation_unavailable");
        TenantApprovalContinuationRules.Require(request, run, source);
    }
}

public sealed class TenantRunContinuationService(ITenantRunStore runs, ITenantApprovalStore approvals,
    TenantApprovedOperationService effects, TimeProvider time, ITenantRuntimeStore? runtimeStore = null, IIdGenerator? ids = null,
    TenantCredentialCheckService? credentialChecks = null, ITenantSandboxCommandExecutor? sandboxCommands = null, TenantSandboxApprovedOperationService? sandboxResults = null, TenantSandboxRuntimeReadService? sandboxReads = null, TenantBrowserEffectService? browserEffects = null)
{
    // Called by the shared-capacity host before mailbox dispatch. ResumeAsync consumes receipts only for external effects.
    public async ValueTask PrepareExternalEffectAsync(AgentRun dispatched, CancellationToken ct = default)
    {
        var run = await Current(dispatched, ct).ConfigureAwait(false);
        if (run.ApprovalRequestId is not { } requestId) return;
        var tenant = run.Owner.TenantId!.Value;
        var request = await approvals.FindApprovalAsync(tenant, requestId, ct).ConfigureAwait(false);
        var source = run.Admission.ApprovalResume is { } resume ? await runs.FindTenantRunAsync(tenant, resume.SourceRunId, ct).ConfigureAwait(false) : null;
        TenantRunContinuationRules.Require(run, request, source);
        ValidateAuthorizationCheckpoint(run, request!, (request!.Operation.Kind is TenantOperationKind.CredentialConnectionCheck or TenantOperationKind.SandboxCommand or TenantOperationKind.BrowserCommand)
            && run.Admission.ApprovalResume is not null && run.SideEffect.ToolCallId != "operation:" + request.Operation.OperationId.ToString("D"));
        if (request!.Status != TenantApprovalStatus.Approved || request.Operation.Kind is not (TenantOperationKind.CredentialConnectionCheck or TenantOperationKind.SandboxCommand or TenantOperationKind.BrowserCommand)) return;
        var remaining = run.Checkpoint?.PauseBudgetClock(time.GetUtcNow()).RemainingOverallBudgetMs
            ?? (run.Admission.ExecutionBudget?.Profile.DurationSeconds ?? 180) * 1000;
        if (remaining <= 0) throw new AccessDeniedException("execution_budget_exhausted");
        using var budget = new CancellationTokenSource(TimeSpan.FromMilliseconds(remaining), time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
        try
        {
            if (request.Operation.Kind == TenantOperationKind.BrowserCommand)
                await (browserEffects ?? throw new AccessDeniedException("tenant_effect_unavailable")).ExecuteAsync(new(tenant, run.AgentRunId), requestId, request.Operation.OperationId,
                    run.Claim!.Generation, request.Operation.ActionHash, linked.Token).ConfigureAwait(false);
            else if (request.Operation.Kind == TenantOperationKind.SandboxCommand)
                await (sandboxCommands ?? throw new AccessDeniedException("tenant_effect_unavailable")).ExecuteApprovedAsync(
                    new(run.Owner, run.SessionId, run.AgentRunId, run.Claim!.Generation), requestId, linked.Token).ConfigureAwait(false);
            else await (credentialChecks ?? throw new AccessDeniedException("tenant_effect_unavailable")).ExecuteAsync(new(tenant, run.AgentRunId), requestId, request.Operation.OperationId,
                run.Claim!.Generation, request.Operation.ActionHash, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested && !ct.IsCancellationRequested) { }
    }

    public async ValueTask<(AgentRun Run, TenantApprovalOutcome? Outcome)> ResumeAsync(AgentRun dispatched, CancellationToken ct = default)
    {
        var tenant = dispatched.Owner.TenantId ?? throw new AccessDeniedException("tenant_context_required");
        var run = await Current(dispatched, ct).ConfigureAwait(false);
        if (run.ApprovalRequestId is not { } requestId) return (run, null);
        var request = await approvals.FindApprovalAsync(tenant, requestId, ct).ConfigureAwait(false);
        var source = run.Admission.ApprovalResume is { } resume ? await runs.FindTenantRunAsync(tenant, resume.SourceRunId, ct).ConfigureAwait(false) : null;
        TenantRunContinuationRules.Require(run, request, source);
        ValidateAuthorizationCheckpoint(run, request!);
        if (browserEffects is not null) await browserEffects.RequireStoredObservationAsync(run, ct).ConfigureAwait(false);
        TenantOperationReceipt? receipt = null;
        if (request!.Status == TenantApprovalStatus.Approved)
            receipt = await effects.ExecuteAsync(new(tenant, run.AgentRunId), requestId, request.Operation.OperationId,
                run.Claim!.Generation, request.Operation.ActionHash, ct).ConfigureAwait(false);
        run = await Current(dispatched, ct).ConfigureAwait(false);
        var reason = receipt?.ReasonCode ?? request.Status switch
        {
            TenantApprovalStatus.Rejected => "approval_rejected",
            TenantApprovalStatus.Expired => "approval_expired",
            TenantApprovalStatus.Cancelled => "approval_cancelled",
            _ => throw new SecurityConflictException("approval_continuation_unavailable")
        };
        var outcome = new TenantApprovalOutcome(tenant, run.AgentRunId, requestId, request.Operation.OperationId, request.Operation.InstanceId,
            request.RootActivationId, request.Status, receipt?.Outcome ?? TenantOperationOutcome.Denied,
            reason, receipt?.InstanceRevision, receipt?.PersonaRevision);
        if (run.Checkpoint is { } checkpoint && AgentRunToolCallCheckpoint.TryRead(checkpoint, out var transcript))
        {
            var pending = AgentRunToolCallCheckpoint.PendingCalls(transcript!);
            if (pending.Any(c => c.Name == ToolCatalog.RequestAuthorization))
            {
                ValidateAuthorizationCheckpoint(run, request);
                if (runtimeStore is null || ids is null)
                    throw new SecurityConflictException("approval_continuation_mismatch");
                var sandbox = request.Operation.Kind == TenantOperationKind.SandboxCommand && receipt is not null
                    ? sandboxResults is null ? new TenantSandboxResultRead(null, "tenant_effect_unavailable")
                        : await sandboxResults.ReadResultAsync(new(run.Owner, run.SessionId, run.AgentRunId, run.Claim!.Generation), request, receipt, ct).ConfigureAwait(false)
                    : null;
                string? inspection = null; string? readUnavailable = null;
                if (request.Operation.SandboxRead is not null && receipt?.Outcome == TenantOperationOutcome.Completed)
                {
                    try
                    {
                        inspection = await (sandboxReads ?? throw new AccessDeniedException("tenant_effect_unavailable")).InspectApprovedForRunAsync(
                            new(run.Owner, run.SessionId, run.AgentRunId, run.Claim!.Generation), request, receipt, ToolLimits.MaxOutputBytes, ct).ConfigureAwait(false);
                    }
                    catch (AccessDeniedException e) { readUnavailable = e.ReasonCode; }
                    catch (SecurityConflictException e) { readUnavailable = e.ReasonCode; }
                }
                var text = JsonSerializer.Serialize(new { approvalStatus = JsonNamingPolicy.CamelCase.ConvertName(request.Status.ToString()), outcome = JsonNamingPolicy.CamelCase.ConvertName(outcome.Outcome.ToString()),
                    reasonCode = reason, operationId = outcome.OperationId, applied = request.Operation.Kind == TenantOperationKind.SandboxCommand && outcome.Outcome != TenantOperationOutcome.Denied ? null : outcome.Outcome switch {
                        TenantOperationOutcome.Completed => (bool?)true, TenantOperationOutcome.Denied => false, _ => null } });
                if (request.Operation.SandboxControl is not null)
                    text = JsonSerializer.Serialize(new { approvalStatus = JsonNamingPolicy.CamelCase.ConvertName(request.Status.ToString()),
                        outcome = JsonNamingPolicy.CamelCase.ConvertName(outcome.Outcome.ToString()), reasonCode = reason, operationId = outcome.OperationId,
                        applied = outcome.Outcome == TenantOperationOutcome.Completed, controlRequestAccepted = outcome.Outcome == TenantOperationOutcome.Completed,
                        terminationConfirmed = false, filesRetained = true });
                var paused = checkpoint.PauseBudgetClock(time.GetUtcNow());
                var resumed = request.Operation.BrowserCommand is not null
                    ? TenantBrowserContinuationProjection.Complete(paused, pending[0], request, outcome.Outcome, reason, null,
                        request.Status == TenantApprovalStatus.Approved ? "browser_observation_not_retained" : "browser_command_not_dispatched")
                    : request.Operation.SandboxRead is not null ? TenantSandboxContinuationProjection.CompleteRead(paused, pending[0], request, outcome, inspection, readUnavailable)
                    : sandbox is null ? AgentRunToolCallCheckpoint.CompleteTenantAuthorization(paused, pending[0], text)
                    : TenantSandboxContinuationProjection.Complete(paused, pending[0], request, outcome, sandbox);
                run = await runtimeStore.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
                    new AgentRunCommand.Checkpoint(run.Revision, time.GetUtcNow(), run.Claim!.Generation, resumed, null), ids.NewId(), ct, run.Claim.Generation).ConfigureAwait(false);
            }
        }
        return (run, outcome);
    }

    internal static void ValidateAuthorizationCheckpoint(AgentRun run, TenantApprovalRequest request, bool requirePending = false)
    {
        if (run.Checkpoint is null || !AgentRunToolCallCheckpoint.TryRead(run.Checkpoint, out var transcript))
        {
            if (requirePending) throw new SecurityConflictException("approval_continuation_mismatch");
            return;
        }
        var pending = AgentRunToolCallCheckpoint.PendingCalls(transcript!);
        if (!pending.Any(c => c.Name == ToolCatalog.RequestAuthorization))
        {
            if (requirePending) throw new SecurityConflictException("approval_continuation_mismatch");
            return;
        }
        try
        {
            if (pending.Count != 1 || !TenantApprovalToolContract.Parse(pending[0]).Matches(request.Operation))
                throw new ArgumentException("Pending authorization differs from the saved operation.");
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException) { throw new SecurityConflictException("approval_continuation_mismatch"); }
    }

    private async ValueTask<AgentRun> Current(AgentRun dispatched, CancellationToken ct)
    {
        var run = await runs.FindTenantRunAsync(dispatched.Owner.TenantId!.Value, dispatched.AgentRunId, ct).ConfigureAwait(false);
        if (run is null || run.Owner != dispatched.Owner || run.SessionId != dispatched.SessionId
            || run.Status != AgentRunStatus.Running || run.Claim?.Generation != dispatched.Claim?.Generation
            || run.CancellationRequested || run.Claim!.LeaseExpiresAtUtc <= time.GetUtcNow())
            throw new AccessDeniedException("tenant_runtime_unavailable");
        return run;
    }
}
