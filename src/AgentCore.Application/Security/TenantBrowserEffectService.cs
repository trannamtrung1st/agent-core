using AgentCore.Application.Ports;
using AgentCore.Application.Execution;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

// Direct callers receive transient observations; reviewed model output is retained only in its exact Run checkpoint.
public sealed record TenantBrowserEffectResult(TenantOperationReceipt Receipt, BrowserResult? Result, string? ObservationUnavailableReason = null);

public sealed partial class TenantBrowserEffectService(IIamStore security, ITenantRunStore runs, ITenantApprovalStore approvals,
    ITenantApprovedOperationStore effects, TenantBrowserOperationTarget targets, IamAuthorizer authorizer, IBrowser browser,
    TenantOperationAuthorizer operations, IIdGenerator ids, TimeProvider time, ITenantArtifactStore? artifacts = null)
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
        if (request.Operation.BrowserCommand is not { } command || request.Status != TenantApprovalStatus.Approved
            || request.Kind == ApprovalKind.ExceptionalGrant && command.Credential is null
            || request.Operation.OperationId != operationId || request.Operation.ActionHash != actionHash)
            throw new SecurityConflictException("operation_mismatch");
        if (run.Status == AgentRunStatus.Running && run.Claim?.Generation != generation) throw new AccessDeniedException("tenant_runtime_unavailable");
        var old = await effects.FindOperationReceiptAsync(reference.TenantId, operationId, ct).ConfigureAwait(false);
        if (old is not null) return new(Matching(old, request, run), null, "browser_observation_not_retained");
        RequireClaim(run, generation);
        TenantRunContinuationService.ValidateAuthorizationCheckpoint(run, request);
        if (run.SideEffect.Disposition is AgentRunSideEffectDisposition.InFlight or AgentRunSideEffectDisposition.Indeterminate)
            throw new SecurityConflictException("browser_effect_in_progress");
        var state = await security.ReadTenantAsync(reference.TenantId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        var decision = await effects.FindApprovalDecisionAsync(reference.TenantId, requestId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("approval_unavailable");
        var grant = await approvals.FindOperationGrantAsync(reference.TenantId, requestId, ct).ConfigureAwait(false);
        var commit = new TenantOperationCommit(requestId, operationId, run.AgentRunId, generation, run.Revision, request.Revision, decision.DecisionId,
            grant?.Revision, actionHash, TenantOperationOutcome.Completed, "operation_completed");
        TenantBrowserEffectRules.Start(request, decision, grant, run, commit, source);
        string? reason = request.ExpiresAt <= time.GetUtcNow() ? "approval_expired" : grant?.ExpiresAt <= time.GetUtcNow() ? "grant_expired" : null; TenantBrowserCommandWitness? witness = null;
        try
        {
            if (reason is null)
            {
                var agent = await targets.RequireSourceAsync(request, run, source, ct).ConfigureAwait(false);
                await RequireAuthorityAsync(state, request, decision, run, agent, ct).ConfigureAwait(false);
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
                    TenantDispatchCheck = token => GateAsync(request, decision, fence, token),
                    TenantUploadResolver = command.ArtifactSourcesJson is null ? null : async token => {
                        if (await GateAsync(request, decision, fence, token).ConfigureAwait(false) is { } denied) throw new AccessDeniedException(denied);
                        var files = await targets.UploadsAsync(request, token).ConfigureAwait(false);
                        if (await GateAsync(request, decision, fence, token).ConfigureAwait(false) is { } changed) throw new AccessDeniedException(changed);
                        return files;
                    },
                    TenantPasswordResolver = command.Credential is null ? null : async (origin, token) => {
                        if (await GateAsync(request, decision, fence, token).ConfigureAwait(false) is { } denied) throw new AccessDeniedException(denied);
                        var live = await runs.FindTenantRunAsync(reference.TenantId, run.AgentRunId, token).ConfigureAwait(false) ?? throw new AccessDeniedException("run_unavailable");
                        RequireClaim(live, generation);
                        return await targets.PasswordAsync(request, live, origin, token).ConfigureAwait(false);
                    } }, linked.Token).AsTask();
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
        TenantBrowserEffectObservation? observation = null;
        if (outcome == TenantOperationOutcome.Completed && result is not null)
        {
            try
            {
                var observedRun = await runs.FindTenantRunAsync(reference.TenantId, run.AgentRunId, settleDeadline.Token).ConfigureAwait(false) ?? throw new AccessDeniedException("run_unavailable");
                RequireClaim(observedRun, generation);
                var observedState = await security.ReadTenantAsync(reference.TenantId, settleDeadline.Token).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
                var observedAgent = await targets.RequireSourceAsync(request, observedRun, source, settleDeadline.Token).ConfigureAwait(false);
                await RequireAuthorityAsync(observedState, request, decision, observedRun, observedAgent, settleDeadline.Token, started: true).ConfigureAwait(false);
                observation = new(observedRun.Revision, TenantBrowserContinuationProjection.Observation(result));
            }
            catch (Exception e) when (e is AccessDeniedException or SecurityConflictException) { }
        }
        var publication = command.Artifacts is not null && outcome == TenantOperationOutcome.Completed && result is not null
            ? TenantBrowserArtifactRules.Capture(request, result) : null;
        var receipt = await SettleAsync(fence, outcome, reason, false, settleDeadline.Token, observation, publication).ConfigureAwait(false) ?? throw new SecurityConflictException("browser_effect_unsettled");
        ct.ThrowIfCancellationRequested();
        if (receipt.Outcome != TenantOperationOutcome.Completed || outcome != TenantOperationOutcome.Completed) return new(receipt, null);
        try
        {
            var current = await runs.FindTenantRunAsync(reference.TenantId, run.AgentRunId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("run_unavailable");
            RequireClaim(current, generation);
            var currentState = await security.ReadTenantAsync(reference.TenantId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
            var agent = await targets.RequireSourceAsync(request, current, source, ct).ConfigureAwait(false);
            await RequireAuthorityAsync(currentState, request, decision, current, agent, ct, receipt: receipt).ConfigureAwait(false);
            if ((await security.ReadTenantAsync(reference.TenantId, ct).ConfigureAwait(false))?.Tenant.Revision != currentState.Tenant.Revision)
                throw new SecurityConflictException("stale_revision");
            var finalRun = await runs.FindTenantRunAsync(reference.TenantId, run.AgentRunId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("run_unavailable");
            RequireClaim(finalRun, generation);
            if (finalRun.Revision != current.Revision
                || (await security.ReadTenantAsync(reference.TenantId, ct).ConfigureAwait(false))?.Tenant.Revision != currentState.Tenant.Revision)
                throw new SecurityConflictException("stale_revision");
            return new(receipt, result! with { Downloads = null, Bytes = null,
                DataJson = TenantBrowserContinuationProjection.ArtifactData(result.DataJson, receipt.BrowserArtifactsJson) });
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
            TenantBrowserEffectRules.RequireFence(fence, request, run, source, await approvals.FindOperationGrantAsync(fence.TenantId, fence.RequestId, ct).ConfigureAwait(false)); RequireClaim(run, fence.Origin.Generation);
            if (request.ExpiresAt <= time.GetUtcNow()) return "approval_expired";
            var state = await security.ReadTenantAsync(fence.TenantId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
            var agent = await targets.RequireSourceAsync(request, run, source, ct).ConfigureAwait(false);
            await RequireAuthorityAsync(state, request, decision, run, agent, ct, started: true).ConfigureAwait(false);
            if ((await security.ReadTenantAsync(fence.TenantId, ct).ConfigureAwait(false))?.Tenant.Revision != state.Tenant.Revision) return "browser_source_changed";
            var finalRun = await runs.FindTenantRunAsync(fence.TenantId, fence.RunId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("run_unavailable");
            TenantBrowserEffectRules.RequireFence(fence, request, finalRun, source, await approvals.FindOperationGrantAsync(fence.TenantId, fence.RequestId, ct).ConfigureAwait(false)); RequireClaim(finalRun, fence.Origin.Generation);
            if ((await security.ReadTenantAsync(fence.TenantId, ct).ConfigureAwait(false))?.Tenant.Revision != state.Tenant.Revision) return "browser_source_changed";
            return request.ExpiresAt <= time.GetUtcNow() ? "approval_expired" : null;
        }
        catch (AccessDeniedException e) { return TenantBrowserEffectRules.Denial(e.ReasonCode); }
        catch (SecurityConflictException e) { return TenantBrowserEffectRules.Denial(e.ReasonCode); }
    }
    // A saved observation is historical tool data, never permission for another command.
    // Recheck its delivery scope before every resumed model generation as well as first delivery.
    public async ValueTask RequireStoredObservationAsync(AgentRun run, CancellationToken ct = default)
    {
        if (run.Checkpoint is null || !AgentRunToolCallCheckpoint.TryRead(run.Checkpoint, out var messages)) return;
        var observations = new HashSet<(Guid RequestId, Guid OperationId)>();
        foreach (var message in messages!.Where(m => m.Role == ModelRole.Tool && m.Name == AgentCore.Application.Tools.ToolCatalog.RequestAuthorization))
        {
            using var doc = System.Text.Json.JsonDocument.Parse(message.Text); var root = doc.RootElement;
            if (!root.TryGetProperty("browser", out var data) || data.ValueKind == System.Text.Json.JsonValueKind.Null) continue;
            if (!root.TryGetProperty("requestId", out var requestId) || !requestId.TryGetGuid(out var id)
                || !root.TryGetProperty("operationId", out var operationId) || !operationId.TryGetGuid(out var operation))
                throw new AccessDeniedException("browser_source_changed");
            observations.Add((id, operation));
        }
        if (observations.Count == 0) return;
        var tenant = run.Owner.TenantId!.Value;
        var current = await runs.FindTenantRunAsync(tenant, run.AgentRunId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("run_unavailable");
        RequireClaim(current, run.Claim!.Generation);
        var state = await security.ReadTenantAsync(tenant, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        foreach (var observation in observations)
        {
            var request = await approvals.FindApprovalAsync(tenant, observation.RequestId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("approval_unavailable");
            if (request.Operation.OperationId != observation.OperationId) throw new AccessDeniedException("browser_source_changed");
            var agent = await targets.RequireObservationSourceAsync(request, current, ct).ConfigureAwait(false);
            var decision = await effects.FindApprovalDecisionAsync(tenant, request.RequestId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("approval_unavailable");
            var receipt = await effects.FindOperationReceiptAsync(tenant, observation.OperationId, ct).ConfigureAwait(false)
                ?? throw new AccessDeniedException("browser_source_changed");
            await RequireAuthorityAsync(state, request, decision, current, agent, ct, receipt: receipt).ConfigureAwait(false);
        }
        if ((await security.ReadTenantAsync(tenant, ct).ConfigureAwait(false))?.Tenant.Revision != state.Tenant.Revision)
            throw new AccessDeniedException("browser_source_changed");
        var finalRun = await runs.FindTenantRunAsync(tenant, run.AgentRunId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("run_unavailable");
        RequireClaim(finalRun, run.Claim.Generation);
        if (finalRun.Revision != current.Revision) throw new AccessDeniedException("browser_source_changed");
    }

    private void RequireClaim(AgentRun run, Guid generation)
    {
        if (run.Status != AgentRunStatus.Running || run.CancellationRequested || run.Claim?.Generation != generation || run.Claim.LeaseExpiresAtUtc <= time.GetUtcNow())
            throw new AccessDeniedException("execution_claim_expired");
    }
    private async ValueTask RequireAuthorityAsync(TenantSecurityState state, TenantApprovalRequest request, TenantApprovalDecision decision,
        AgentRun run, TenantAgent agent, CancellationToken ct, bool started = false, TenantOperationReceipt? receipt = null)
    {
        var grant = await approvals.FindOperationGrantAsync(request.TenantId, request.RequestId, ct).ConfigureAwait(false);
        await TenantBrowserEffectRules.RequireAuthorityAsync(state, request, decision, run, agent, authorizer, ct, grant,
            receipt is not null ? OperationGrantStatus.Consumed : started ? OperationGrantStatus.Claimed : OperationGrantStatus.Available,
            time.GetUtcNow(), receipt).ConfigureAwait(false);
    }
    public async ValueTask<int> RecoverAsync(CancellationToken ct = default)
    {
        var count = 0;
        foreach (var fence in await effects.RecoverableBrowserEffectsAsync(time.GetUtcNow(), 100, ct).ConfigureAwait(false))
            if (await SettleAsync(fence, TenantOperationOutcome.Indeterminate, TenantBrowserEffectRules.Uncertain, true, ct).ConfigureAwait(false) is not null) count++;
        return count;
    }
    private async ValueTask<TenantOperationReceipt?> SettleAsync(TenantBrowserEffectFence fence, TenantOperationOutcome outcome, string reason, bool recovery, CancellationToken ct, TenantBrowserEffectObservation? observation = null, TenantBrowserArtifactBatch? artifacts = null)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var state = await security.ReadTenantAsync(fence.TenantId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
            var request = await approvals.FindApprovalAsync(fence.TenantId, fence.RequestId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("approval_unavailable");
            var receipt = await effects.TrySettleBrowserEffectAsync(Next(state), state.Tenant.Revision, fence, outcome, reason, Audit(request, TenantBrowserEffectRules.Action, reason), recovery, ct, observation, artifacts).ConfigureAwait(false);
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
