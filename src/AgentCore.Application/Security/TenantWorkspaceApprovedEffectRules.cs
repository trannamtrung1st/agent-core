using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantWorkspaceApprovedEffectRules
{
    public const string Completed = TenantWorkspaceEffectRules.Completed;
    public const string Uncertain = TenantWorkspaceEffectRules.Uncertain;
    public const string NotApplied = "workspace_mutation_not_applied";
    private static readonly HashSet<string> Denials = new(StringComparer.Ordinal) { NotApplied, "approval_expired", "grant_expired", "grant_unavailable",
        "grantor_authority_lost", "operation_authority_changed", "operation_witness_changed", "workspace_source_changed", "workspace_observation_unavailable",
        "executor_read_required", "executor_unavailable", "requester_unavailable", "tenant_unavailable", "resource_unavailable", "root_operation_conflict",
        "independent_reviewer_required", "approver_unqualified", "permission_denied", "capability_unavailable", "home_access_disabled", "home_read_only", "execution_claim_expired" };
    public static string Denial(string reason) => Denials.Contains(reason) ? reason : "workspace_source_changed";
    private static DateTimeOffset Milliseconds(DateTimeOffset at) => DateTimeOffset.FromUnixTimeMilliseconds(at.ToUnixTimeMilliseconds());
    public static void Start(TenantApprovalRequest request, TenantApprovalDecision decision, TenantOperationGrant? grant, AgentRun run,
        TenantOperationCommit commit, AgentRun? source)
    {
        TenantLocalEffectRules.ValidateApproval(request, decision, grant, run, commit, source);
        RequireGrant(request, grant, OperationGrantStatus.Available);
        if (request.Operation.WorkspaceMutation is null || commit.Outcome is not (TenantOperationOutcome.Completed or TenantOperationOutcome.Denied)
            || (commit.Outcome == TenantOperationOutcome.Completed ? commit.ReasonCode != "operation_completed" : !Denials.Contains(commit.ReasonCode)))
            throw new SecurityConflictException("operation_mismatch");
    }
    public static void RequireGrant(TenantApprovalRequest request, TenantOperationGrant? grant, OperationGrantStatus status)
    {
        if (!Enum.IsDefined(request.Kind) || request.Operation.WorkspaceMutation is null) throw new SecurityConflictException("grant_mismatch");
        if (request.Kind != ApprovalKind.ExceptionalGrant) { if (grant is not null) throw new SecurityConflictException("grant_mismatch"); return; }
        var permission = PermissionCatalog.Get(request.Permission);
        if (!permission.Delegatable || permission.HumanOnly || permission.SystemOnly || request.Permission is not ("workspaces.write" or "workspaces.delete")
            || status is not (OperationGrantStatus.Available or OperationGrantStatus.Claimed) || grant is null || grant.Status != status
            || grant.TenantId != request.TenantId || grant.RequestId != request.RequestId || grant.ExecutorPrincipalId != request.ExecutorPrincipalId
            || grant.GrantId == Guid.Empty || grant.Revision < 1 || grant.OperationId != request.Operation.OperationId || grant.ActionHash != request.Operation.ActionHash
            || Milliseconds(grant.ExpiresAt) != Milliseconds(request.ExpiresAt) || grant.EffectIdempotencyKey != "operation:" + request.Operation.OperationId.ToString("D"))
            throw new SecurityConflictException("grant_unavailable");
    }
    public static async ValueTask RequireAuthorityAsync(TenantSecurityState state, TenantApprovalRequest request, TenantApprovalDecision decision,
        TenantAgent agent, IamAuthorizer iam, TenantOperationGrant? grant, OperationGrantStatus status, DateTimeOffset at, CancellationToken ct)
    {
        if (state.Tenant.TenantId != request.TenantId || state.Tenant.Status != IdentityStatus.Active) throw new AccessDeniedException("tenant_unavailable");
        TenantSessionRules.RequireExecutor(state, agent);
        if (request.Status != TenantApprovalStatus.Approved || decision.Status != TenantApprovalStatus.Approved || decision.RequestId != request.RequestId
            || decision.TenantId != request.TenantId || decision.ActionHash != request.Operation.ActionHash || decision.RequestRevision != request.Revision)
            throw new SecurityConflictException("operation_mismatch");
        RequireGrant(request, grant, status);
        if (request.ExpiresAt <= at) throw new AccessDeniedException("approval_expired");
        if (grant?.ExpiresAt <= at) throw new AccessDeniedException("grant_expired");
        await TenantWorkspaceMutationOperationRules.ExecutorAsync(state, request, iam, ct).ConfigureAwait(false);
        try { await TenantWorkspaceMutationOperationRules.ReviewerAsync(state, request, decision.HumanPrincipalId, iam, ct).ConfigureAwait(false); }
        catch (AccessDeniedException) when (request.Kind == ApprovalKind.ExceptionalGrant) { throw new AccessDeniedException("grantor_authority_lost"); }
    }
    public static TenantApprovedWorkspaceDispatch Fence(TenantApprovalRequest request, AgentRun run) => new(request.TenantId, request.RequestId,
        request.Operation.OperationId, run.AgentRunId, request.Operation.ActionHash, run.SideEffect.ExternalEffect ?? throw new SecurityConflictException("workspace_effect_fence_required"));
    public static void RequireFence(TenantApprovedWorkspaceDispatch fence, TenantApprovalRequest request, AgentRun run, AgentRun? source, TenantOperationGrant? grant)
    {
        TenantRunContinuationRules.Require(run, request, source);
        if (request.Operation.WorkspaceMutation is null || request.Status != TenantApprovalStatus.Approved || fence != Fence(request, run)
            || fence.Origin.DirectOperation is not null || run.SideEffect.ToolCallId != "operation:" + fence.OperationId.ToString("D")
            || run.SideEffect.ActionHash != fence.ActionHash || run.Revision < fence.Origin.StartedRunRevision
            || run.SideEffect.Disposition is not (AgentRunSideEffectDisposition.InFlight or AgentRunSideEffectDisposition.Indeterminate))
            throw new SecurityConflictException("operation_mismatch");
        RequireGrant(request, grant, OperationGrantStatus.Claimed);
    }
    public static bool Recoverable(AgentRun run, DateTimeOffset at) => run.SideEffect.Disposition == AgentRunSideEffectDisposition.Indeterminate
        || run.Status != AgentRunStatus.Running || run.Claim is null || run.Claim.Generation != run.SideEffect.ExternalEffect?.Generation || run.Claim.LeaseExpiresAtUtc <= at;
    public static void Transaction(TenantSecurityState next, long revision, TenantApprovalRequest request, AuthorizationAudit audit, bool dispatch)
    {
        if (next.Tenant.TenantId != request.TenantId || next.Tenant.Revision != checked(revision + 1) || audit.TenantId != request.TenantId
            || audit.ActorPrincipalId != request.ExecutorPrincipalId || audit.Target != request.ResourceId
            || audit.Action != (dispatch ? TenantWorkspaceEffectRules.Dispatch : TenantWorkspaceEffectRules.Result)) throw new ArgumentException("Invalid workspace effect transaction.");
    }
    public static void Outcome(TenantOperationOutcome outcome, string reason, bool recovery)
    {
        if (recovery ? outcome != TenantOperationOutcome.Indeterminate || reason != Uncertain : outcome switch {
            TenantOperationOutcome.Completed => reason != Completed, TenantOperationOutcome.Denied => !Denials.Contains(reason),
            TenantOperationOutcome.Indeterminate => reason != Uncertain, _ => true }) throw new ArgumentException("Invalid factual workspace outcome.");
    }
    public static AgentRun SettleRun(AgentRun run, TenantApprovedWorkspaceDispatch fence, TenantOperationOutcome outcome, DateTimeOffset at)
    {
        if (run.Status != AgentRunStatus.Running || run.CancellationRequested || run.Claim?.Generation != fence.Origin.Generation
            || run.SideEffect.Disposition != AgentRunSideEffectDisposition.InFlight) return run;
        return run.MarkSideEffect(run.Revision, fence.Origin.Generation, outcome switch { TenantOperationOutcome.Completed => AgentRunSideEffectDisposition.Succeeded,
            TenantOperationOutcome.Denied => AgentRunSideEffectDisposition.DefinitelyFailed, _ => AgentRunSideEffectDisposition.Indeterminate },
            "operation:" + fence.OperationId.ToString("D"), fence.ActionHash, at);
    }
    public static TenantOperationReceipt Receipt(TenantApprovalRequest request, AgentRun run, TenantOperationOutcome outcome, string reason, DateTimeOffset at) =>
        new(request.Operation.OperationId, request.RequestId, request.TenantId, run.AgentRunId, run.SessionId, request.RootActivationId, request.ExecutorPrincipalId,
            request.Operation.InstanceId, request.Operation.ActionHash, outcome, reason, request.Operation.ExpectedInstanceRevision, request.Operation.ExpectedPersonaRevision, Milliseconds(at));
    public static TenantOperationReceipt Matching(TenantOperationReceipt receipt, TenantApprovedWorkspaceDispatch fence)
    {
        if (receipt.TenantId != fence.TenantId || receipt.RequestId != fence.RequestId || receipt.OperationId != fence.OperationId || receipt.RunId != fence.RunId
            || receipt.ActionHash != fence.ActionHash || receipt.SourceCallId is not null || receipt.ParametersHash is not null) throw new SecurityConflictException("operation_mismatch");
        return receipt;
    }
}
