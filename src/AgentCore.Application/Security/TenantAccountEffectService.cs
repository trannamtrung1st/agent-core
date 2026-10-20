using System.Text.Json;
using AgentCore.Application.Drive;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantAccountEffectService(IIamStore security, ITenantRunStore runs, ITenantApprovalStore approvals,
    ITenantApprovedOperationStore receipts, ITenantAccountOperationStore effects, TenantAccountActionTarget targets, IamAuthorizer iam,
    TenantOperationAuthorizer operations, ITenantConnectedAccountTransportFactory transports, DriveService drive,
    IEmailStore drafts, ITenantWorkspaceMutationStore files, IIdGenerator ids, TimeProvider time)
{
    public async ValueTask<TenantOperationReceipt> ExecuteApprovedAsync(TenantRunReference reference, Guid requestId, Guid operationId,
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
    private async ValueTask<TenantOperationReceipt> ExecuteCoreAsync(TenantRunReference reference, Guid requestId, Guid operationId,
        Guid generation, string actionHash, CancellationToken ct)
    {
        var run = await runs.FindTenantRunAsync(reference.TenantId, reference.RunId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("run_unavailable");
        var request = await approvals.FindApprovalAsync(reference.TenantId, requestId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("approval_unavailable");
        var source = await SourceRun(request, run, ct).ConfigureAwait(false); TenantRunContinuationRules.Require(run, request, source);
        if (request.Status != TenantApprovalStatus.Approved || request.Operation.AccountAction is null || request.Operation.OperationId != operationId
            || request.Operation.ActionHash != actionHash || run.Status == AgentRunStatus.Running && run.Claim?.Generation != generation)
            throw new SecurityConflictException("operation_mismatch");
        var old = await receipts.FindOperationReceiptAsync(reference.TenantId, operationId, ct).ConfigureAwait(false);
        if (old is not null) return Matching(old, request, run);
        RequireClaim(run, generation); TenantRunContinuationService.ValidateAuthorizationCheckpoint(run, request);
        if (run.SideEffect.Disposition is AgentRunSideEffectDisposition.InFlight or AgentRunSideEffectDisposition.Indeterminate)
            throw new SecurityConflictException("account_effect_in_progress");
        var state = await security.ReadTenantAsync(reference.TenantId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        var decision = await receipts.FindApprovalDecisionAsync(reference.TenantId, requestId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("approval_unavailable");
        var grant = await approvals.FindOperationGrantAsync(reference.TenantId, requestId, ct).ConfigureAwait(false);
        var commit = new TenantOperationCommit(requestId, operationId, run.AgentRunId, generation, run.Revision, request.Revision, decision.DecisionId,
            grant?.Revision, actionHash, TenantOperationOutcome.Completed, "operation_completed");
        TenantAccountEffectRules.Start(request, decision, grant, run, commit, source);
        string? reason = null;
        try
        {
            var target = await targets.SourceAsync(new(run.Owner, run.SessionId, run.AgentRunId, generation), request.Operation.AccountAction.Credential.Reference, ct).ConfigureAwait(false);
            await targets.RequireSavedAsync(request, run, ct).ConfigureAwait(false);
            await TenantAccountEffectRules.RequireAuthorityAsync(state, request, decision, target.Agent, iam, grant, OperationGrantStatus.Available, time.GetUtcNow(), ct).ConfigureAwait(false);
        }
        catch (AccessDeniedException e) { reason = TenantAccountEffectRules.Denial(e.ReasonCode); }
        catch (SecurityConflictException e) { reason = TenantAccountEffectRules.Denial(e.ReasonCode); }
        if (reason is not null) commit = commit with { Outcome = TenantOperationOutcome.Denied, ReasonCode = reason };
        var start = await effects.TryStartAccountEffectAsync(Next(state), state.Tenant.Revision, commit,
            Audit(request, reason is null ? TenantAccountEffectRules.DispatchAction : TenantAccountEffectRules.ResultAction, reason ?? "account_action_dispatched"), ct).ConfigureAwait(false);
        if (start?.Receipt is { } local) return Matching(local, request, run);
        if (start?.Fence is not { } fence)
        {
            old = await receipts.FindOperationReceiptAsync(reference.TenantId, operationId, ct).ConfigureAwait(false);
            return old is not null ? Matching(old, request, run) : throw new SecurityConflictException("account_effect_in_progress");
        }
        var action = request.Operation.AccountAction; var args = TenantAccountActionRules.Arguments(action);
        var initial = await targets.SourceAsync(new(run.Owner, run.SessionId, run.AgentRunId, generation), args.Reference, ct).ConfigureAwait(false);
        var expected = initial.Bound.Credential.Value;
        async ValueTask<TenantAccountActionSource> Current(CancellationToken token)
        {
            if (await receipts.FindOperationReceiptAsync(fence.TenantId, fence.OperationId, token).ConfigureAwait(false) is not null) throw new AccessDeniedException("account_effect_already_settled");
            var current = await targets.SourceAsync(new(run.Owner, run.SessionId, run.AgentRunId, generation), args.Reference, token).ConfigureAwait(false);
            targets.Require(current, action, exactSecret: false);
            if (!TenantConnectedAccountRules.Same(expected, current.Bound.Credential.Value)) throw new SecurityConflictException("account_source_changed");
            TenantApprovalRules.Validate(request, current.Run, current.Agent, source: source);
            var claimed = await approvals.FindOperationGrantAsync(fence.TenantId, requestId, token).ConfigureAwait(false);
            TenantAccountEffectRules.RequireFence(fence, request, current.Run, source, claimed);
            await TenantAccountEffectRules.RequireAuthorityAsync(current.State, request, decision, current.Agent, iam, claimed, OperationGrantStatus.Claimed, time.GetUtcNow(), token).ConfigureAwait(false);
            if ((await approvals.FindRootApprovalAsync(fence.TenantId, request.RootActivationId, request.ExecutorPrincipalId, request.Operation, token).ConfigureAwait(false))?.RequestId != requestId)
                throw new AccessDeniedException("root_operation_conflict");
            return current;
        }
        async ValueTask<Credential> Recheck(CancellationToken token) => (await Current(token).ConfigureAwait(false)).Bound.Credential.Value;
        async ValueTask Maintain(CredentialOAuthCheck value, CancellationToken token)
        {
            if (value.RotatedProtectedPayload is null && !value.ReconnectRequired) return;
            var current = await Current(token).ConfigureAwait(false);
            if (!await effects.TryMaintainAccountEffectAsync(Next(current.State), current.State.Tenant.Revision, fence, expected.Revision, expected.SecretRevision, value,
                Audit(request, "operations.accounts.oauth.maintain", value.ReconnectRequired ? "oauth_reconnect_required" : "oauth_grant_rotated"), token).ConfigureAwait(false))
                throw new SecurityConflictException("account_source_changed");
            expected = TenantCredentialOAuthRules.Checked(expected, value, time.GetUtcNow());
        }
        var duration = run.Claim!.LeaseExpiresAtUtc - time.GetUtcNow() - TimeSpan.FromSeconds(1);
        var perTool = TimeSpan.FromSeconds(run.Admission.ExecutionBudget?.Profile.PerToolSeconds ?? 30); if (duration > perTool) duration = perTool;
        var consent = request.ExpiresAt - time.GetUtcNow(); if (duration > consent) duration = consent;
        if (run.Checkpoint?.PauseBudgetClock(time.GetUtcNow()).RemainingOverallBudgetMs is { } remaining && duration.TotalMilliseconds > remaining) duration = TimeSpan.FromMilliseconds(remaining);
        var outcome = TenantOperationOutcome.Denied; reason = TenantAccountEffectRules.NotApplied; TenantAccountActionResult? result = null;
        Task<NativeOutcome>? pending = null;
        using (var deadline = new CancellationTokenSource(duration > TimeSpan.Zero ? duration : TimeSpan.FromMilliseconds(1), time))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token))
        {
            try
            {
                await Current(linked.Token).ConfigureAwait(false);
                var native = await transports.OpenAsync(new(fence.TenantId, run.AgentInstanceId, initial.Bound.Binding, expected), Recheck, Maintain,
                    args.ToolName == ToolCatalog.EmailSend, linked.Token).ConfigureAwait(false);
                // No native command can be reached until the durable reservation exists.
                pending = ApplyAsync(request, run, native, Current, linked.Token).AsTask();
                var observed = await pending.WaitAsync(linked.Token).ConfigureAwait(false);
                outcome = observed.Outcome; result = observed.Result;
                reason = observed.Reason ?? (outcome == TenantOperationOutcome.Completed ? TenantAccountEffectRules.Completed : outcome == TenantOperationOutcome.Denied ? TenantAccountEffectRules.NotApplied : TenantAccountEffectRules.Uncertain);
            }
            catch (Exception)
            {
                // Once the native owner is entered, absence is uncertainty. Never call it again.
                if (pending is not null) { _ = Observe(pending); outcome = TenantOperationOutcome.Indeterminate; reason = TenantAccountEffectRules.Uncertain; }
            }
        }
        using var settlement = new CancellationTokenSource(TimeSpan.FromSeconds(10), time);
        var receipt = await Settle(fence, outcome, reason, false, settlement.Token, result).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested(); return receipt ?? throw new SecurityConflictException("account_effect_unsettled");
    }
    public async ValueTask<int> RecoverAsync(CancellationToken ct = default)
    {
        var count = 0;
        foreach (var fence in await effects.RecoverableAccountEffectsAsync(time.GetUtcNow(), 100, ct).ConfigureAwait(false))
            if (await Settle(fence, TenantOperationOutcome.Indeterminate, TenantAccountEffectRules.Uncertain, true, ct).ConfigureAwait(false) is not null) count++;
        return count;
    }
    private async ValueTask<TenantOperationReceipt?> Settle(TenantAccountDispatch fence, TenantOperationOutcome outcome, string reason, bool recovery,
        CancellationToken ct, TenantAccountActionResult? result = null)
    {
        var request = await approvals.FindApprovalAsync(fence.TenantId, fence.RequestId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("approval_unavailable");
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var state = await security.ReadTenantAsync(fence.TenantId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
            var saved = await effects.TrySettleAccountEffectAsync(Next(state), state.Tenant.Revision, fence, outcome, reason,
                Audit(request, TenantAccountEffectRules.ResultAction, reason), recovery, ct, result).ConfigureAwait(false);
            if (saved is not null) return saved;
        }
        return null;
    }
    private void RequireClaim(AgentRun run, Guid generation)
    {
        if (run.Status != AgentRunStatus.Running || run.CancellationRequested || run.Claim?.Generation != generation || run.Claim.LeaseExpiresAtUtc <= time.GetUtcNow())
            throw new AccessDeniedException("tenant_runtime_unavailable");
    }
    private async ValueTask<AgentRun?> SourceRun(TenantApprovalRequest request, AgentRun run, CancellationToken ct) => request.RunId == run.AgentRunId ? null
        : await runs.FindTenantRunAsync(request.TenantId, request.RunId, ct).ConfigureAwait(false);
    private AuthorizationAudit Audit(TenantApprovalRequest request, string action, string reason) => new(ids.NewId(), request.TenantId, request.ExecutorPrincipalId,
        action, request.ResourceId, "allowed", reason, time.GetUtcNow());
    private static TenantSecurityState Next(TenantSecurityState state) => state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } };
    private static async Task Observe(Task task) { try { await task.ConfigureAwait(false); } catch (Exception) { } }
    private static TenantOperationReceipt Matching(TenantOperationReceipt receipt, TenantApprovalRequest request, AgentRun run)
    {
        if (receipt.TenantId != request.TenantId || receipt.RequestId != request.RequestId || receipt.OperationId != request.Operation.OperationId || receipt.RunId != run.AgentRunId
            || receipt.SessionId != run.SessionId || receipt.InstanceId != request.Operation.InstanceId || receipt.ExecutorPrincipalId != request.ExecutorPrincipalId
            || receipt.RootActivationId != request.RootActivationId || receipt.ActionHash != request.Operation.ActionHash) throw new SecurityConflictException("operation_mismatch");
        return receipt;
    }
}
