using System.Text;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantWorkspaceMutationEffectService
{
    // Trusted off-mailbox path. Every native argument comes from the exact saved consent.
    public async ValueTask<TenantOperationReceipt> ExecuteApprovedAsync(TenantRunReference reference, Guid requestId, Guid operationId,
        Guid generation, string actionHash, CancellationToken ct = default)
    {
        var run = await runs.FindTenantRunAsync(reference.TenantId, reference.RunId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("run_unavailable");
        var request = await approvals.FindApprovalAsync(reference.TenantId, requestId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("approval_unavailable");
        var source = request.RunId == run.AgentRunId ? null : await runs.FindTenantRunAsync(reference.TenantId, request.RunId, ct).ConfigureAwait(false);
        TenantRunContinuationRules.Require(run, request, source);
        if (request.Status != TenantApprovalStatus.Approved || request.Operation.WorkspaceMutation is null || request.Operation.OperationId != operationId
            || request.Operation.ActionHash != actionHash) throw new SecurityConflictException("operation_mismatch");
        if (run.Status == AgentRunStatus.Running && run.Claim?.Generation != generation) throw new AccessDeniedException("tenant_runtime_unavailable");
        var old = await receipts.FindOperationReceiptAsync(reference.TenantId, operationId, ct).ConfigureAwait(false);
        if (old is not null) return MatchApproved(old, request, run);
        RequireApprovedClaim(run, generation);
        if (run.SideEffect.Disposition is AgentRunSideEffectDisposition.Prepared or AgentRunSideEffectDisposition.InFlight or AgentRunSideEffectDisposition.Indeterminate
            || run.SideEffect.ToolCallId == "operation:" + operationId.ToString("D")) throw new SecurityConflictException("workspace_effect_in_progress");
        var state = await security.ReadTenantAsync(reference.TenantId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        var decision = await receipts.FindApprovalDecisionAsync(reference.TenantId, requestId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("approval_unavailable");
        var grant = await approvals.FindOperationGrantAsync(reference.TenantId, requestId, ct).ConfigureAwait(false);
        var commit = new TenantOperationCommit(requestId, operationId, run.AgentRunId, generation, run.Revision, request.Revision, decision.DecisionId,
            grant?.Revision, actionHash, TenantOperationOutcome.Completed, "operation_completed");
        TenantWorkspaceApprovedEffectRules.Start(request, decision, grant, run, commit, source);
        string? reason = null;
        try
        {
            var current = await configurations.CurrentPolicyAsync(reference.TenantId, run.AgentInstanceId, ct).ConfigureAwait(false);
            await TenantWorkspaceApprovedEffectRules.RequireAuthorityAsync(state, request, decision, current.Agent, iam, grant, OperationGrantStatus.Available, time.GetUtcNow(), ct).ConfigureAwait(false);
            await targets.RequireSavedAsync(request, run, ct).ConfigureAwait(false);
        }
        catch (AccessDeniedException e) { reason = TenantWorkspaceApprovedEffectRules.Denial(e.ReasonCode); }
        catch (SecurityConflictException e) when (e.ReasonCode != "stale_revision") { reason = TenantWorkspaceApprovedEffectRules.Denial(e.ReasonCode); }
        if (reason is not null) commit = commit with { Outcome = TenantOperationOutcome.Denied, ReasonCode = reason };
        var start = await effects.TryStartApprovedWorkspaceEffectAsync(Next(state), state.Tenant.Revision, commit,
            ApprovedAudit(request, reason is null, reason ?? "workspace_mutation_dispatched"), ct).ConfigureAwait(false);
        if (start?.Receipt is { } denied) return MatchApproved(denied, request, run);
        if (start?.Fence is not { } fence)
        {
            old = await receipts.FindOperationReceiptAsync(reference.TenantId, operationId, ct).ConfigureAwait(false);
            return old is not null ? MatchApproved(old, request, run) : throw new SecurityConflictException("workspace_effect_in_progress");
        }
        var args = TenantWorkspaceMutationOperationRules.Arguments(request.Operation.WorkspaceMutation);
        var outcome = TenantOperationOutcome.Indeterminate; var dispatched = false; var sourceChecked = false; Task<(TenantOperationOutcome Outcome, TenantPublicDownloadReceipt? Download)>? pending = null; TenantPublicDownloadReceipt? download = null;
        reason = TenantWorkspaceApprovedEffectRules.Uncertain;
        var duration = run.Claim!.LeaseExpiresAtUtc - time.GetUtcNow() - TimeSpan.FromSeconds(1);
        var perTool = TimeSpan.FromSeconds(run.Admission.ExecutionBudget?.Profile.PerToolSeconds ?? 30); if (duration > perTool) duration = perTool;
        var consent = request.ExpiresAt - time.GetUtcNow(); if (duration > consent) duration = consent;
        if (run.Checkpoint?.PauseBudgetClock(time.GetUtcNow()).RemainingOverallBudgetMs is { } remaining && duration.TotalMilliseconds > remaining) duration = TimeSpan.FromMilliseconds(remaining);
        using (var deadline = new CancellationTokenSource(duration > TimeSpan.Zero ? duration : TimeSpan.FromMilliseconds(1), time))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token))
        {
            try
            {
                async ValueTask Check(CancellationToken token)
                {
                    // Structural effects have an ordered/partial prefix; do not compare their own prefix to the initial footprint.
                    var observe = !sourceChecked || args.Tool is ToolCatalog.WorkspaceWrite or ToolCatalog.WorkspacePatch
                        || args.Tool == ToolCatalog.WebDownload && TenantPublicDownloadContract.Read(args).Path is not null;
                    await ApprovedGate(request, decision, fence, observe, token).ConfigureAwait(false); sourceChecked = true;
                }
                await ApprovedGate(request, decision, fence, true, linked.Token).ConfigureAwait(false); linked.Token.ThrowIfCancellationRequested(); dispatched = true;
                pending = ApplyApproved(run, operationId, args, Check, linked.Token).AsTask();
                var applied = await pending.WaitAsync(linked.Token).ConfigureAwait(false); outcome = applied.Outcome; download = applied.Download; linked.Token.ThrowIfCancellationRequested();
                reason = outcome == TenantOperationOutcome.Completed ? TenantWorkspaceApprovedEffectRules.Completed : outcome == TenantOperationOutcome.Denied
                    ? TenantWorkspaceApprovedEffectRules.NotApplied : TenantWorkspaceApprovedEffectRules.Uncertain;
            }
            catch (Exception) { if (pending is not null) _ = ObserveApproved(pending); outcome = dispatched ? TenantOperationOutcome.Indeterminate : TenantOperationOutcome.Denied;
                reason = dispatched ? TenantWorkspaceApprovedEffectRules.Uncertain : TenantWorkspaceApprovedEffectRules.NotApplied; }
        }
        if (outcome != TenantOperationOutcome.Completed) download = null;
        using var settlement = new CancellationTokenSource(TimeSpan.FromSeconds(10), time);
        TenantOperationReceipt? receipt;
        try { receipt = await SettleApproved(fence, outcome, reason, false, settlement.Token, download).ConfigureAwait(false); }
        catch (Exception) { receipt = null; }
        ct.ThrowIfCancellationRequested();
        return receipt ?? throw new SecurityConflictException("workspace_effect_unsettled");
    }
    private async ValueTask<(TenantOperationOutcome Outcome, TenantPublicDownloadReceipt? Download)> ApplyApproved(AgentRun run, Guid operationId, TenantWorkspaceMutationArguments args, Func<CancellationToken, ValueTask> check, CancellationToken ct)
    {
        if (args.Tool == ToolCatalog.WebDownload) return await Download(run, operationId, args, check, ct).ConfigureAwait(false);
        if (TenantOfficeMutationContract.IsMutation(args.Tool))
        {
            if (office is null) throw new AccessDeniedException("office_processing_unavailable");
            await files.TransformOfficeTenantAsync(TenantWorkspaceEffectRules.Owner(run, args.Path!), args.Path!, !TenantOfficeMutationContract.IsEdit(args.Tool),
                (source, token) => TenantOfficeMutationContract.Process(office, args, source, token).Bytes, check, ct).ConfigureAwait(false);
        }
        else if (args.Tool == ToolCatalog.WorkspaceWrite)
            await files.WriteTenantAsync(TenantWorkspaceEffectRules.Owner(run, args.Path!), args.Path!, Encoding.UTF8.GetBytes(args.Content!), check, ct).ConfigureAwait(false);
        else if (args.Tool == ToolCatalog.WorkspacePatch)
            await files.PatchTenantAsync(TenantWorkspaceEffectRules.Owner(run, args.Path!), args.Path!, args.Edits, check, ct).ConfigureAwait(false);
        else if (args.Tool == ToolCatalog.WorkspaceCopy)
        {
            var copy = args.Operations[0];
            await files.CopyTenantAsync(TenantWorkspaceEffectRules.Owner(run, copy.Source!), copy.Source!, TenantWorkspaceEffectRules.Owner(run, copy.Destination!), copy.Destination!, check, ct).ConfigureAwait(false);
        }
        else
        {
            var result = await files.StructureTenantAsync(TenantWorkspaceEffectRules.Owner(run, args.Operations[0].Path ?? args.Operations[0].Source!), args.Operations, check, ct).ConfigureAwait(false);
            return (result.Completed ? TenantOperationOutcome.Completed : result.MutationsMayHaveOccurred ? TenantOperationOutcome.Indeterminate : TenantOperationOutcome.Denied, null);
        }
        return (TenantOperationOutcome.Completed, null);
    }
    private static async Task ObserveApproved(Task pending) { try { await pending.ConfigureAwait(false); } catch (Exception) { } }
    private async ValueTask ApprovedGate(TenantApprovalRequest request, TenantApprovalDecision decision, TenantApprovedWorkspaceDispatch fence, bool observe, CancellationToken ct)
    {
        if (await receipts.FindOperationReceiptAsync(fence.TenantId, fence.OperationId, ct).ConfigureAwait(false) is not null) throw new SecurityConflictException("workspace_effect_already_settled");
        var (run, state) = await operations.ResolveAsync(new(fence.TenantId, fence.RunId), ct).ConfigureAwait(false);
        var source = request.RunId == run.AgentRunId ? null : await runs.FindTenantRunAsync(fence.TenantId, request.RunId, ct).ConfigureAwait(false);
        var grant = await approvals.FindOperationGrantAsync(fence.TenantId, fence.RequestId, ct).ConfigureAwait(false);
        TenantWorkspaceApprovedEffectRules.RequireFence(fence, request, run, source, grant); RequireApprovedClaim(run, fence.Origin.Generation);
        var current = await configurations.CurrentPolicyAsync(fence.TenantId, run.AgentInstanceId, ct).ConfigureAwait(false);
        TenantApprovalRules.Validate(request, run, current.Agent, source: source);
        TenantWorkspaceMutationOperationRules.Require(request, run, current.Agent, current.Definition,
            await sessions.FindTenantSessionAsync(fence.TenantId, run.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable"), source);
        await TenantWorkspaceApprovedEffectRules.RequireAuthorityAsync(state, request, decision, current.Agent, iam, grant, OperationGrantStatus.Claimed, time.GetUtcNow(), ct).ConfigureAwait(false);
        if (observe) await targets.RequireSavedAsync(request, run, ct).ConfigureAwait(false);
        if ((await approvals.FindRootApprovalAsync(fence.TenantId, request.RootActivationId, request.ExecutorPrincipalId, request.Operation, ct).ConfigureAwait(false))?.RequestId != request.RequestId)
            throw new AccessDeniedException("root_operation_conflict");
        var final = await runs.FindTenantRunAsync(fence.TenantId, fence.RunId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("run_unavailable");
        TenantWorkspaceApprovedEffectRules.RequireFence(fence, request, final, source, await approvals.FindOperationGrantAsync(fence.TenantId, fence.RequestId, ct).ConfigureAwait(false));
        RequireApprovedClaim(final, fence.Origin.Generation);
        if (final.Revision != run.Revision || (await security.ReadTenantAsync(fence.TenantId, ct).ConfigureAwait(false))?.Tenant.Revision != state.Tenant.Revision)
            throw new SecurityConflictException("workspace_source_changed");
    }
    private async ValueTask<TenantOperationReceipt?> SettleApproved(TenantApprovedWorkspaceDispatch fence, TenantOperationOutcome outcome, string reason, bool recovery, CancellationToken ct, TenantPublicDownloadReceipt? download = null)
    {
        var request = await approvals.FindApprovalAsync(fence.TenantId, fence.RequestId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("approval_unavailable");
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var state = await security.ReadTenantAsync(fence.TenantId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
            var receipt = await effects.TrySettleApprovedWorkspaceEffectAsync(Next(state), state.Tenant.Revision, fence, outcome, reason, ApprovedAudit(request, false, reason), recovery, ct, download).ConfigureAwait(false);
            if (receipt is not null) return receipt;
        }
        return null;
    }
    private AuthorizationAudit ApprovedAudit(TenantApprovalRequest request, bool dispatch, string reason) => new(ids.NewId(), request.TenantId, request.ExecutorPrincipalId,
        dispatch ? TenantWorkspaceEffectRules.Dispatch : TenantWorkspaceEffectRules.Result, request.ResourceId, "allowed", reason, time.GetUtcNow());
    private void RequireApprovedClaim(AgentRun run, Guid generation)
    {
        if (run.Status != AgentRunStatus.Running || run.CancellationRequested || run.Claim?.Generation != generation || run.Claim.LeaseExpiresAtUtc <= time.GetUtcNow())
            throw new AccessDeniedException("tenant_runtime_unavailable");
    }
    private static TenantOperationReceipt MatchApproved(TenantOperationReceipt receipt, TenantApprovalRequest request, AgentRun run)
    {
        if (receipt.TenantId != request.TenantId || receipt.RequestId != request.RequestId || receipt.OperationId != request.Operation.OperationId || receipt.RunId != run.AgentRunId
            || receipt.SessionId != run.SessionId || receipt.RootActivationId != request.RootActivationId || receipt.ExecutorPrincipalId != request.ExecutorPrincipalId
            || receipt.InstanceId != request.Operation.InstanceId || receipt.ActionHash != request.Operation.ActionHash || receipt.SourceCallId is not null || receipt.ParametersHash is not null)
            throw new SecurityConflictException("operation_mismatch");
        return receipt;
    }
}
