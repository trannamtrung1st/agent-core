using System.Text;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantWorkspaceMutationResult(OperationDecisionKind Decision, string ReasonCode, TenantOperationReceipt? Receipt = null, Guid? UnsettledOperationId = null)
{
    public bool IsUncertain => Receipt?.Outcome == TenantOperationOutcome.Indeterminate || UnsettledOperationId is not null;
    public Guid? OperationId => Receipt?.OperationId ?? UnsettledOperationId;
}

public sealed class TenantWorkspaceMutationEffectService(TenantOperationAuthorizer operations, IIamStore security, ITenantRunStore runs,
    ITenantSessionStore sessions, TenantRunConfigurationResolver configurations, IamAuthorizer iam, ITenantWorkspaceOperationStore effects,
    ITenantWorkspaceMutationStore files, IIdGenerator ids, TimeProvider time, ITenantApprovalStore approvals)
{
    public async ValueTask<TenantWorkspaceMutationResult> ExecuteAsync(TenantRuntimeFrame frame, ModelToolCall call, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(call.Id) || call.Id.Length > 200) throw new ArgumentException("Use a bounded trusted call identity.");
        var args = TenantWorkspaceMutationContract.Parse(frame.SessionId, call.Name, call.ArgumentsJson);
        var reference = new TenantRunReference(frame.Owner.TenantId ?? throw new AccessDeniedException("tenant_context_required"), frame.RunId);
        var (run, state) = await operations.ResolveForDecisionAsync(reference, "workspaces.write", ExecutionAuthorizationPurpose.EffectEligibility, ct).ConfigureAwait(false);
        var session = await sessions.FindTenantSessionAsync(reference.TenantId, frame.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        TenantRuntimeRules.Require(frame, session.Owner, run, time.GetUtcNow());
        var old = await effects.FindDirectOperationReceiptAsync(reference.TenantId, frame.RunId, call.Id, ct).ConfigureAwait(false);
        if (old is not null) return Result(TenantWorkspaceEffectRules.Match(old, run, call.Id, args.ParametersHash));
        var prior = await RootApproval(run, args, ct).ConfigureAwait(false);
        if (prior is not null) return new(prior.Status == TenantApprovalStatus.Pending ? OperationDecisionKind.RequiresApproval : OperationDecisionKind.Denied,
            TenantApprovalRules.DuplicateReason(prior));
        if (run.SideEffect.Disposition is AgentRunSideEffectDisposition.Prepared or AgentRunSideEffectDisposition.InFlight or AgentRunSideEffectDisposition.Indeterminate)
            throw new SecurityConflictException("workspace_effect_in_progress");
        var current = await configurations.CurrentPolicyAsync(reference.TenantId, run.AgentInstanceId, ct).ConfigureAwait(false);
        var op = new AgentRunDirectOperation(ids.NewId(), call.Id, call.Name, args.CanonicalArguments, args.ParametersHash, current.Agent.Instance.Revision, current.Agent.Instance.PersonaRevision);
        TenantWorkspaceEffectRules.Session(run, current.Agent, session);
        _ = TenantWorkspaceEffectRules.Validate(run, current.Agent, current.Definition, op, frame.Generation, time.GetUtcNow());
        foreach (var (key, scope) in TenantWorkspaceEffectRules.Targets(run, args))
        {
            if (key.EndsWith(".read", StringComparison.Ordinal) && !(await iam.CheckOwnedTenantAsync(state, current.Agent.PrincipalId, PermissionCatalog.Get(key), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("executor_read_required");
            var decision = await operations.Check(state, run, key, scope, ct).ConfigureAwait(false);
            if (decision.Decision != OperationDecisionKind.Allowed) return new(decision.Decision, decision.ReasonCode);
        }
        await TenantWorkspaceEffectRules.RequireAuthority(state, run, args, iam, ct).ConfigureAwait(false);
        var commit = new TenantWorkspaceEffectCommit(run.AgentRunId, frame.Generation, run.Revision, op, TenantWorkspaceMutationContract.ActionHash(run.SessionId, args.ParametersHash));
        var start = await effects.TryStartWorkspaceEffectAsync(Next(state), state.Tenant.Revision, commit, Audit(run, args, true, "workspace_mutation_dispatched"), ct).ConfigureAwait(false);
        if (start?.Receipt is { } existing) return Result(existing);
        if (start?.Fence is not { } fence)
        {
            old = await effects.FindDirectOperationReceiptAsync(reference.TenantId, frame.RunId, call.Id, ct).ConfigureAwait(false);
            return old is not null ? Result(TenantWorkspaceEffectRules.Match(old, run, call.Id, args.ParametersHash)) : throw new SecurityConflictException("workspace_effect_in_progress");
        }
        var outcome = TenantOperationOutcome.Indeterminate; var dispatched = false;
        var duration = TimeSpan.FromSeconds(run.Admission.ExecutionBudget?.Profile.PerToolSeconds ?? 30);
        var lease = run.Claim!.LeaseExpiresAtUtc - time.GetUtcNow() - TimeSpan.FromSeconds(1); if (duration > lease) duration = lease;
        if (run.Checkpoint?.PauseBudgetClock(time.GetUtcNow()).RemainingOverallBudgetMs is { } remaining && duration.TotalMilliseconds > remaining) duration = TimeSpan.FromMilliseconds(remaining);
        using (var deadline = new CancellationTokenSource(duration > TimeSpan.Zero ? duration : TimeSpan.FromMilliseconds(1), time))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token))
        {
            try
            {
                await Gate(fence, linked.Token).ConfigureAwait(false); linked.Token.ThrowIfCancellationRequested(); dispatched = true;
                ValueTask Check(CancellationToken token) => Gate(fence, token);
                if (call.Name == ToolCatalog.WorkspaceWrite)
                    await files.WriteTenantAsync(TenantWorkspaceEffectRules.Owner(run, args.Path!), args.Path!, Encoding.UTF8.GetBytes(args.Content!), Check, linked.Token).ConfigureAwait(false);
                else if (call.Name == ToolCatalog.WorkspacePatch)
                    await files.PatchTenantAsync(TenantWorkspaceEffectRules.Owner(run, args.Path!), args.Path!, args.Edits, Check, linked.Token).ConfigureAwait(false);
                else if (call.Name == ToolCatalog.WorkspaceCopy)
                {
                    var copy = args.Operations[0];
                    await files.CopyTenantAsync(TenantWorkspaceEffectRules.Owner(run, copy.Source!), copy.Source!, TenantWorkspaceEffectRules.Owner(run, copy.Destination!), copy.Destination!, Check, linked.Token).ConfigureAwait(false);
                }
                else
                {
                    var result = await files.StructureTenantAsync(TenantWorkspaceEffectRules.Owner(run, args.Operations[0].Path ?? args.Operations[0].Source!), args.Operations, Check, linked.Token).ConfigureAwait(false);
                    outcome = result.Completed ? TenantOperationOutcome.Completed : result.MutationsMayHaveOccurred ? TenantOperationOutcome.Indeterminate : TenantOperationOutcome.Denied;
                }
                if (call.Name is ToolCatalog.WorkspaceWrite or ToolCatalog.WorkspacePatch or ToolCatalog.WorkspaceCopy) outcome = TenantOperationOutcome.Completed;
            }
            catch (Exception) { outcome = dispatched ? TenantOperationOutcome.Indeterminate : TenantOperationOutcome.Denied; }
        }
        using var settlement = new CancellationTokenSource(TimeSpan.FromSeconds(10), time);
        TenantOperationReceipt? receipt;
        try { receipt = await Settle(fence, outcome, false, settlement.Token).ConfigureAwait(false); }
        catch (Exception) { receipt = null; } // Retained dispatch is authoritative; an unavailable receipt must never imply safe replay.
        ct.ThrowIfCancellationRequested();
        return receipt is not null ? Result(receipt) : new(OperationDecisionKind.Allowed, TenantWorkspaceEffectRules.Uncertain, UnsettledOperationId: op.OperationId);
    }
    private async ValueTask Gate(TenantWorkspaceDispatch fence, CancellationToken ct)
    {
        var (run, state) = await operations.ResolveAsync(new(fence.TenantId, fence.RunId), ct).ConfigureAwait(false);
        if (await effects.FindDirectOperationReceiptAsync(fence.TenantId, fence.RunId, fence.Origin.DirectOperation!.SourceCallId, ct).ConfigureAwait(false) is not null) throw new SecurityConflictException("workspace_effect_already_settled");
        TenantWorkspaceEffectRules.RequireFence(run, fence);
        var current = await configurations.CurrentPolicyAsync(fence.TenantId, run.AgentInstanceId, ct).ConfigureAwait(false);
        var args = TenantWorkspaceEffectRules.Validate(run, current.Agent, current.Definition, fence.Origin.DirectOperation!, fence.Origin.Generation, time.GetUtcNow(), true);
        TenantWorkspaceEffectRules.Session(run, current.Agent, await sessions.FindTenantSessionAsync(fence.TenantId, run.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable"));
        if (await RootApproval(run, args, ct).ConfigureAwait(false) is { } prior) throw new AccessDeniedException(TenantApprovalRules.DuplicateReason(prior));
        await TenantWorkspaceEffectRules.RequireAuthority(state, run, args, iam, ct).ConfigureAwait(false);
        var final = await runs.FindTenantRunAsync(fence.TenantId, fence.RunId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("run_unavailable");
        TenantWorkspaceEffectRules.RequireFence(final, fence);
        if (final.Claim?.Generation != fence.Origin.Generation || final.CancellationRequested || final.Status != AgentRunStatus.Running || final.Claim.LeaseExpiresAtUtc <= time.GetUtcNow()
            || (await security.ReadTenantAsync(fence.TenantId, ct).ConfigureAwait(false))?.Tenant.Revision != state.Tenant.Revision) throw new SecurityConflictException("workspace_source_changed");
    }
    private ValueTask<TenantApprovalRequest?> RootApproval(AgentRun run, TenantWorkspaceMutationArguments args, CancellationToken ct) =>
        approvals.FindRootApprovalAsync(run.Owner.TenantId!.Value, run.Admission.Activation.RootActivationId, run.Owner.ExecutorPrincipalId!.Value,
            TenantWorkspaceMutationOperationRules.Correlation(run, args), ct);
    public async ValueTask<int> RecoverAsync(CancellationToken ct = default)
    {
        var count = 0;
        foreach (var fence in await effects.RecoverableWorkspaceEffectsAsync(time.GetUtcNow(), 100, ct).ConfigureAwait(false))
            if (await Settle(fence, TenantOperationOutcome.Indeterminate, true, ct).ConfigureAwait(false) is not null) count++;
        return count;
    }
    private async ValueTask<TenantOperationReceipt?> Settle(TenantWorkspaceDispatch fence, TenantOperationOutcome outcome, bool recovery, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var state = await security.ReadTenantAsync(fence.TenantId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
            var op = fence.Origin.DirectOperation!; var args = TenantWorkspaceMutationContract.Parse(fence.SessionId, op.ToolName, op.ArgumentsJson);
            var reason = outcome == TenantOperationOutcome.Completed ? TenantWorkspaceEffectRules.Completed : outcome == TenantOperationOutcome.Denied ? "workspace_mutation_not_applied" : TenantWorkspaceEffectRules.Uncertain;
            var path = args.Targets.First(t => t.Permission != "workspaces.read").Path;
            var target = TenantWorkspaceRules.Scope(new(fence.TenantId, fence.Owner.AgentInstanceId, path.StartsWith("/working/", StringComparison.Ordinal) ? fence.SessionId : null));
            var audit = new AuthorizationAudit(ids.NewId(), fence.TenantId, fence.Owner.ExecutorPrincipalId!.Value, TenantWorkspaceEffectRules.Result, target.ResourceId!, "allowed", reason, time.GetUtcNow());
            var receipt = await effects.TrySettleWorkspaceEffectAsync(Next(state), state.Tenant.Revision, fence, outcome, reason, audit, recovery, ct).ConfigureAwait(false);
            if (receipt is not null) return receipt;
        }
        return null;
    }
    private AuthorizationAudit Audit(AgentRun run, TenantWorkspaceMutationArguments args, bool dispatch, string reason) => new(ids.NewId(), run.Owner.TenantId, run.Owner.ExecutorPrincipalId!.Value,
        dispatch ? TenantWorkspaceEffectRules.Dispatch : TenantWorkspaceEffectRules.Result, TenantWorkspaceRules.Scope(TenantWorkspaceEffectRules.Owner(run, args.Targets.First(t => t.Permission != "workspaces.read").Path)).ResourceId!,
        "allowed", reason, time.GetUtcNow());
    private static TenantSecurityState Next(TenantSecurityState state) => state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } };
    private static TenantWorkspaceMutationResult Result(TenantOperationReceipt receipt) => new(OperationDecisionKind.Allowed, receipt.ReasonCode, receipt);
}
