using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

// Native observations are transient here. Durable model output/checkpoint delivery is a separate adapter.
public sealed record TenantBrowserEffectResult(TenantOperationReceipt Receipt, BrowserResult? Result, string? ObservationUnavailableReason = null);

public sealed class TenantBrowserEffectService(IIamStore security, ITenantRunStore runs, ITenantApprovalStore approvals,
    ITenantApprovedOperationStore effects, TenantBrowserOperationTarget targets, IamAuthorizer authorizer, IBrowser browser,
    TenantOperationAuthorizer operations, IIdGenerator ids, TimeProvider time)
{
    public async ValueTask<TenantBrowserEffectResult> ExecuteAsync(TenantRunReference reference, Guid requestId, Guid operationId,
        Guid generation, string actionHash, CancellationToken ct = default)
    {
        try { return await ExecuteCoreAsync(reference, requestId, operationId, generation, actionHash, ct).ConfigureAwait(false); }
        catch (Exception e) when (e is AccessDeniedException or SecurityConflictException)
        {
            var request = await approvals.FindApprovalAsync(reference.TenantId, requestId, ct).ConfigureAwait(false);
            await operations.RecordFailureAsync(reference, request?.Operation.OperationId == operationId ? request.Permission : "",
                e is AccessDeniedException denied ? denied.ReasonCode : ((SecurityConflictException)e).ReasonCode, ExecutionAuthorizationPurpose.EffectEligibility, ct).ConfigureAwait(false);
            throw;
        }
    }
    private async ValueTask<TenantBrowserEffectResult> ExecuteCoreAsync(TenantRunReference reference, Guid requestId, Guid operationId,
        Guid generation, string actionHash, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var startedAt = time.GetTimestamp();
        var run = await runs.FindTenantRunAsync(reference.TenantId, reference.RunId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("run_unavailable");
        var request = await approvals.FindApprovalAsync(reference.TenantId, requestId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("approval_unavailable");
        var source = await SourceAsync(request, run, ct).ConfigureAwait(false); TenantRunContinuationRules.Require(run, request, source);
        if (request.Operation.BrowserCommand is not { } command || request.Status != TenantApprovalStatus.Approved || request.Kind != ApprovalKind.Review
            || request.Operation.OperationId != operationId || request.Operation.ActionHash != actionHash)
            throw new SecurityConflictException("operation_mismatch");
        if (run.Status == AgentRunStatus.Running && run.Claim?.Generation != generation) throw new AccessDeniedException("tenant_runtime_unavailable");
        var old = await effects.FindOperationReceiptAsync(reference.TenantId, operationId, ct).ConfigureAwait(false);
        if (old is not null) return new(Matching(old, request, run), null, "browser_observation_not_retained");
        RequireClaim(run, generation);
        if (run.SideEffect.Disposition is AgentRunSideEffectDisposition.InFlight or AgentRunSideEffectDisposition.Indeterminate)
            throw new SecurityConflictException("browser_effect_in_progress");
        var state = await security.ReadTenantAsync(reference.TenantId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        var decision = await effects.FindApprovalDecisionAsync(reference.TenantId, requestId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("approval_unavailable");
        var commit = new TenantOperationCommit(requestId, operationId, run.AgentRunId, generation, run.Revision, request.Revision, decision.DecisionId,
            null, actionHash, TenantOperationOutcome.Completed, "operation_completed");
        TenantBrowserEffectRules.Start(request, decision, await approvals.FindOperationGrantAsync(reference.TenantId, requestId, ct).ConfigureAwait(false), run, commit, source);
        string? reason = request.ExpiresAt <= time.GetUtcNow() ? "approval_expired" : null; TenantBrowserCommandWitness? witness = null;
        try
        {
            if (reason is null)
            {
                var agent = await targets.RequireSourceAsync(request, run, source, ct).ConfigureAwait(false);
                await TenantBrowserEffectRules.RequireAuthorityAsync(state, request, decision, run, agent, authorizer, ct).ConfigureAwait(false);
                witness = await targets.ResolveApprovedAsync(request, run, ct).ConfigureAwait(false);
            }
        }
        catch (AccessDeniedException e) { reason = TenantBrowserEffectRules.Denial(e.ReasonCode); }
        catch (SecurityConflictException e) { reason = TenantBrowserEffectRules.Denial(e.ReasonCode); }
        if (reason is not null) commit = commit with { Outcome = TenantOperationOutcome.Denied, ReasonCode = reason };
        var audit = Audit(request, reason is null ? TenantBrowserEffectRules.DispatchAction : TenantBrowserEffectRules.Action, reason ?? "browser_command_dispatched");
        var start = await effects.TryStartBrowserEffectAsync(Next(state), state.Tenant.Revision, commit, audit, ct).ConfigureAwait(false);
        if (start?.Receipt is { } deniedReceipt) return new(Matching(deniedReceipt, request, run), null);
        if (start?.Fence is not { } fence)
        {
            old = await effects.FindOperationReceiptAsync(reference.TenantId, operationId, ct).ConfigureAwait(false);
            if (old is not null) return new(Matching(old, request, run), null, "browser_observation_not_retained");
            throw new SecurityConflictException("browser_effect_in_progress");
        }
        BrowserResult? result = null; Task<BrowserResult>? pending = null;
        var duration = run.Claim!.LeaseExpiresAtUtc - time.GetUtcNow() - TimeSpan.FromSeconds(1);
        var perTool = TimeSpan.FromSeconds(Math.Min(30, run.Admission.ExecutionBudget?.Profile.PerToolSeconds ?? 30)) - time.GetElapsedTime(startedAt);
        if (duration > perTool) duration = perTool;
        var consentRemaining = request.ExpiresAt - time.GetUtcNow();
        if (duration > consentRemaining) duration = consentRemaining;
        if (run.Checkpoint?.PauseBudgetClock(time.GetUtcNow()).RemainingOverallBudgetMs is { } overall && duration.TotalMilliseconds > overall)
            duration = TimeSpan.FromMilliseconds(overall);
        if (duration <= TimeSpan.Zero || ct.IsCancellationRequested) result = new("browser_command_not_dispatched");
        else
        {
            using var deadline = new CancellationTokenSource(duration, time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
            try
            {
                var parsed = TenantApprovalRules.BrowserIntent(run.SessionId, command.ToolName, command.ArgumentsJson).Command;
                pending = browser.ExecuteAsync(new(run.SessionId, parsed) { TenantOwner = witness!.Owner, TenantWitness = witness,
                    TenantDispatchCheck = token => GateAsync(request, decision, fence, token) }, linked.Token).AsTask();
                result = await pending.WaitAsync(linked.Token).ConfigureAwait(false);
                if (linked.IsCancellationRequested) result = null;
            }
            catch (Exception) { result = null; if (pending is not null) _ = ObserveAsync(pending); }
        }
        var outcome = result is null || result.EffectAttempted && !result.EffectConfirmedBySdk ? TenantOperationOutcome.Indeterminate
            : result.ErrorCode is null ? TenantOperationOutcome.Completed : result.EffectAttempted ? TenantOperationOutcome.Indeterminate : TenantOperationOutcome.Denied;
        reason = outcome switch { TenantOperationOutcome.Completed => TenantBrowserEffectRules.Completed,
            TenantOperationOutcome.Indeterminate => TenantBrowserEffectRules.Uncertain, _ => TenantBrowserEffectRules.Denial(result!.ErrorCode!) };
        using var settleDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10), time);
        var receipt = await SettleAsync(fence, outcome, reason, false, settleDeadline.Token).ConfigureAwait(false) ?? throw new SecurityConflictException("browser_effect_unsettled");
        ct.ThrowIfCancellationRequested();
        if (receipt.Outcome != TenantOperationOutcome.Completed || outcome != TenantOperationOutcome.Completed) return new(receipt, null);
        try
        {
            var current = await runs.FindTenantRunAsync(reference.TenantId, run.AgentRunId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("run_unavailable");
            RequireClaim(current, generation);
            var currentState = await security.ReadTenantAsync(reference.TenantId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
            var agent = await targets.RequireSourceAsync(request, current, source, ct).ConfigureAwait(false);
            await TenantBrowserEffectRules.RequireAuthorityAsync(currentState, request, decision, current, agent, authorizer, ct).ConfigureAwait(false);
            if ((await security.ReadTenantAsync(reference.TenantId, ct).ConfigureAwait(false))?.Tenant.Revision != currentState.Tenant.Revision)
                throw new SecurityConflictException("stale_revision");
            var finalRun = await runs.FindTenantRunAsync(reference.TenantId, run.AgentRunId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("run_unavailable");
            RequireClaim(finalRun, generation);
            if (finalRun.Revision != current.Revision
                || (await security.ReadTenantAsync(reference.TenantId, ct).ConfigureAwait(false))?.Tenant.Revision != currentState.Tenant.Revision)
                throw new SecurityConflictException("stale_revision");
            return new(receipt, result! with { Downloads = null, Bytes = null });
        }
        catch (Exception e) when (e is AccessDeniedException or SecurityConflictException) { return new(receipt, null, "browser_observation_unavailable"); }
    }
    private async ValueTask<string?> GateAsync(TenantApprovalRequest request, TenantApprovalDecision decision, TenantBrowserEffectFence fence, CancellationToken ct)
    {
        try
        {
            if (await effects.FindOperationReceiptAsync(fence.TenantId, fence.OperationId, ct).ConfigureAwait(false) is not null) return "browser_command_not_dispatched";
            var run = await runs.FindTenantRunAsync(fence.TenantId, fence.RunId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("run_unavailable");
            var source = await SourceAsync(request, run, ct).ConfigureAwait(false);
            TenantBrowserEffectRules.RequireFence(fence, request, run, source); RequireClaim(run, fence.Origin.Generation);
            if (request.ExpiresAt <= time.GetUtcNow()) return "approval_expired";
            var state = await security.ReadTenantAsync(fence.TenantId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
            var agent = await targets.RequireSourceAsync(request, run, source, ct).ConfigureAwait(false);
            await TenantBrowserEffectRules.RequireAuthorityAsync(state, request, decision, run, agent, authorizer, ct).ConfigureAwait(false);
            if ((await security.ReadTenantAsync(fence.TenantId, ct).ConfigureAwait(false))?.Tenant.Revision != state.Tenant.Revision) return "browser_source_changed";
            var finalRun = await runs.FindTenantRunAsync(fence.TenantId, fence.RunId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("run_unavailable");
            TenantBrowserEffectRules.RequireFence(fence, request, finalRun, source); RequireClaim(finalRun, fence.Origin.Generation);
            if ((await security.ReadTenantAsync(fence.TenantId, ct).ConfigureAwait(false))?.Tenant.Revision != state.Tenant.Revision) return "browser_source_changed";
            return request.ExpiresAt <= time.GetUtcNow() ? "approval_expired" : null;
        }
        catch (AccessDeniedException e) { return TenantBrowserEffectRules.Denial(e.ReasonCode); }
        catch (SecurityConflictException e) { return TenantBrowserEffectRules.Denial(e.ReasonCode); }
    }
    private void RequireClaim(AgentRun run, Guid generation)
    {
        if (run.Status != AgentRunStatus.Running || run.CancellationRequested || run.Claim?.Generation != generation || run.Claim.LeaseExpiresAtUtc <= time.GetUtcNow())
            throw new AccessDeniedException("execution_claim_expired");
    }
    public async ValueTask<int> RecoverAsync(CancellationToken ct = default)
    {
        var count = 0;
        foreach (var fence in await effects.RecoverableBrowserEffectsAsync(time.GetUtcNow(), 100, ct).ConfigureAwait(false))
            if (await SettleAsync(fence, TenantOperationOutcome.Indeterminate, TenantBrowserEffectRules.Uncertain, true, ct).ConfigureAwait(false) is not null) count++;
        return count;
    }
    private async ValueTask<TenantOperationReceipt?> SettleAsync(TenantBrowserEffectFence fence, TenantOperationOutcome outcome, string reason, bool recovery, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var state = await security.ReadTenantAsync(fence.TenantId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
            var request = await approvals.FindApprovalAsync(fence.TenantId, fence.RequestId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("approval_unavailable");
            var receipt = await effects.TrySettleBrowserEffectAsync(Next(state), state.Tenant.Revision, fence, outcome, reason, Audit(request, TenantBrowserEffectRules.Action, reason), recovery, ct).ConfigureAwait(false);
            if (receipt is not null) return receipt;
        }
        return null;
    }
    private async ValueTask<AgentRun?> SourceAsync(TenantApprovalRequest request, AgentRun run, CancellationToken ct) => request.RunId == run.AgentRunId ? null
        : await runs.FindTenantRunAsync(request.TenantId, request.RunId, ct).ConfigureAwait(false);
    private AuthorizationAudit Audit(TenantApprovalRequest request, string action, string reason) => new(ids.NewId(), request.TenantId, request.ExecutorPrincipalId, action,
        request.ResourceId, "allowed", reason, time.GetUtcNow());
    private static TenantSecurityState Next(TenantSecurityState state) => state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } };
    private static async Task ObserveAsync(Task task) { try { await task.ConfigureAwait(false); } catch (Exception) { } }
    private static TenantOperationReceipt Matching(TenantOperationReceipt receipt, TenantApprovalRequest request, AgentRun run)
    {
        if (receipt.TenantId != request.TenantId || receipt.RequestId != request.RequestId || receipt.OperationId != request.Operation.OperationId
            || receipt.RunId != run.AgentRunId || receipt.SessionId != run.SessionId || receipt.InstanceId != request.Operation.InstanceId
            || receipt.ExecutorPrincipalId != request.ExecutorPrincipalId || receipt.RootActivationId != request.RootActivationId || receipt.ActionHash != request.Operation.ActionHash)
            throw new SecurityConflictException("operation_mismatch");
        return receipt;
    }
}
