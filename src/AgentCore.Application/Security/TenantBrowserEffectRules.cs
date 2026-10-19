using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantBrowserEffectRules
{
    public const string Action = "operations.browser.command";
    public const string DispatchAction = "operations.browser.command.dispatch";
    public const string Completed = "browser_command_completed";
    public const string Uncertain = "browser_command_uncertain";
    private static readonly HashSet<string> Denials = new(StringComparer.Ordinal) { "approval_expired", "grantor_authority_lost", "permission_denied",
        "executor_unavailable", "requester_unavailable", "tenant_unavailable", "browser_policy_changed", "browser_target_changed", "browser_target_busy",
        "policy_transition_pending", "provider_unavailable", "root_operation_conflict", "operation_witness_changed", "resource_unavailable",
        "browser_file_source_changed", "artifact_content_unavailable", "browser_command_not_dispatched", "independent_reviewer_required", "approver_unqualified", "browser_source_changed", "execution_claim_expired",
        "credential_unavailable", "credential_binding_changed", "credential_origin_changed", "credential_origin_unavailable", "credential_direct_chat_required",
        "credential_target_invalid", "user_intervention_required", "forbidden", "target_denied", "target_missing", "ambiguous_target", "stale_frame",
        "grant_expired", "grant_unavailable", "operation_authority_changed" };
    public static string Denial(string reason) => Denials.Contains(reason) ? reason : "browser_source_changed";
    public static DateTimeOffset Milliseconds(DateTimeOffset at) => DateTimeOffset.FromUnixTimeMilliseconds(at.ToUnixTimeMilliseconds());
    public static void Start(TenantApprovalRequest request, TenantApprovalDecision decision, TenantOperationGrant? grant, AgentRun run,
        TenantOperationCommit commit, AgentRun? source)
    {
        TenantLocalEffectRules.ValidateApproval(request, decision, grant, run, commit, source);
        RequireGrant(request, grant, OperationGrantStatus.Available);
        if (request.Operation.BrowserCommand is null || request.Kind == ApprovalKind.ExceptionalGrant && request.Operation.BrowserCommand.Credential is null
            || commit.Outcome is not (TenantOperationOutcome.Completed or TenantOperationOutcome.Denied)
            || (commit.Outcome == TenantOperationOutcome.Completed ? commit.ReasonCode != "operation_completed" : !Denials.Contains(commit.ReasonCode)))
            throw new SecurityConflictException("operation_mismatch");
    }
    public static TenantBrowserEffectFence Fence(TenantApprovalRequest request, AgentRun run) => new(request.TenantId, request.RequestId,
        request.Operation.OperationId, run.AgentRunId, request.Operation.ActionHash, run.SideEffect.ExternalEffect ?? throw new SecurityConflictException("browser_effect_fence_required"));
    public static void RequireFence(TenantBrowserEffectFence fence, TenantApprovalRequest request, AgentRun run, AgentRun? source, TenantOperationGrant? grant = null)
    {
        TenantRunContinuationRules.Require(run, request, source);
        if (request.Operation.BrowserCommand is null || request.Status != TenantApprovalStatus.Approved
            || fence != Fence(request, run) || run.SideEffect.ToolCallId != "operation:" + fence.OperationId.ToString("D")
            || run.SideEffect.ActionHash != fence.ActionHash || run.Revision < fence.Origin.StartedRunRevision
            || run.SideEffect.Disposition is not (AgentRunSideEffectDisposition.InFlight or AgentRunSideEffectDisposition.Indeterminate))
            throw new SecurityConflictException("operation_mismatch");
        RequireGrant(request, grant, OperationGrantStatus.Claimed);
    }
    public static bool Recoverable(AgentRun run, DateTimeOffset at) => run.SideEffect.Disposition == AgentRunSideEffectDisposition.Indeterminate
        || run.Status != AgentRunStatus.Running || run.Claim is null || run.Claim.Generation != run.SideEffect.ExternalEffect?.Generation || run.Claim.LeaseExpiresAtUtc <= at;
    public static async ValueTask RequireAuthorityAsync(TenantSecurityState state, TenantApprovalRequest request, TenantApprovalDecision decision,
        AgentRun run, TenantAgent agent, IamAuthorizer authorizer, CancellationToken ct, TenantOperationGrant? grant = null,
        OperationGrantStatus expectedGrantStatus = OperationGrantStatus.Available, DateTimeOffset? at = null, TenantOperationReceipt? receipt = null)
    {
        if (state.Tenant.Status != IdentityStatus.Active) throw new AccessDeniedException("tenant_unavailable");
        TenantSessionRules.RequireExecutor(state, agent);
        if (request.Status != TenantApprovalStatus.Approved || decision.Status != TenantApprovalStatus.Approved || decision.RequestId != request.RequestId
            || decision.TenantId != request.TenantId || decision.ActionHash != request.Operation.ActionHash || decision.RequestRevision != request.Revision)
            throw new SecurityConflictException("operation_mismatch");
        RequireGrant(request, grant, expectedGrantStatus);
        if (request.Kind == ApprovalKind.ExceptionalGrant)
        {
            if (expectedGrantStatus == OperationGrantStatus.Consumed)
            {
                if (receipt is null || receipt.Outcome != TenantOperationOutcome.Completed || receipt.TenantId != request.TenantId || receipt.RequestId != request.RequestId
                    || receipt.OperationId != request.Operation.OperationId || receipt.ActionHash != request.Operation.ActionHash || receipt.ExecutorPrincipalId != request.ExecutorPrincipalId
                    || receipt.InstanceId != request.Operation.InstanceId || receipt.SessionId != request.SessionId || receipt.RootActivationId != request.RootActivationId)
                    throw new SecurityConflictException("grant_unavailable");
            }
            else
            {
                if (at is null || grant!.ExpiresAt <= at || request.ExpiresAt <= at) throw new AccessDeniedException("grant_expired");
                if (expectedGrantStatus == OperationGrantStatus.Claimed && (run.SideEffect.ExternalEffect is null
                    || run.SideEffect.ToolCallId != "operation:" + request.Operation.OperationId.ToString("D") || run.SideEffect.ActionHash != request.Operation.ActionHash))
                    throw new SecurityConflictException("grant_unavailable");
            }
        }
        await TenantBrowserOperationRules.RequirePermissionsAsync(state, agent.Instance.InstanceId, request.Operation.BrowserCommand!, request.ExecutorPrincipalId, authorizer, ct,
            request.Kind == ApprovalKind.ExceptionalGrant).ConfigureAwait(false);
        try
        {
            await TenantBrowserOperationRules.RequireReviewerAsync(state, agent.Instance.InstanceId, request.Operation.BrowserCommand!, request.OriginalInitiatorPrincipalId,
                decision.HumanPrincipalId, authorizer, ct, request.Kind).ConfigureAwait(false);
        }
        catch (AccessDeniedException) when (request.Kind == ApprovalKind.ExceptionalGrant) { throw new AccessDeniedException("grantor_authority_lost"); }
        if (request.OriginalInitiatorPrincipalId is { } human)
            foreach (var (permission, scope) in new[] { ("sessions.read", TenantSessionRules.Scope(request.TenantId, run.SessionId)),
                ("agents.chat", AccessScope.ForResource(request.TenantId, "agent", run.AgentInstanceId.ToString("D"))) })
                if (!(await authorizer.CheckTenantAsync(state, human, PermissionCatalog.Get(permission), scope, ct).ConfigureAwait(false)).Allowed)
                    throw new AccessDeniedException("requester_unavailable");
    }
    public static void RequireGrant(TenantApprovalRequest request, TenantOperationGrant? grant, OperationGrantStatus status)
    {
        if (!Enum.IsDefined(request.Kind)) throw new SecurityConflictException("grant_mismatch");
        if (request.Kind != ApprovalKind.ExceptionalGrant)
        {
            if (grant is not null) throw new SecurityConflictException("grant_mismatch");
            return;
        }
        if (status is not (OperationGrantStatus.Available or OperationGrantStatus.Claimed or OperationGrantStatus.Consumed)
            || request.Operation.BrowserCommand?.Credential is null || request.Permission != "credentials.use" || grant is null
            || grant.Status != status || grant.TenantId != request.TenantId || grant.RequestId != request.RequestId || grant.ExecutorPrincipalId != request.ExecutorPrincipalId
            || grant.GrantId == Guid.Empty || grant.Revision < 1 || grant.OperationId != request.Operation.OperationId || grant.ActionHash != request.Operation.ActionHash
            // Grant deadlines are persisted at SQLite's UTC millisecond precision.
            || Milliseconds(grant.ExpiresAt) != Milliseconds(request.ExpiresAt)
            || grant.EffectIdempotencyKey != "operation:" + request.Operation.OperationId.ToString("D"))
            throw new SecurityConflictException("grant_unavailable");
    }
    public static void Transaction(TenantSecurityState next, long revision, TenantApprovalRequest request, AuthorizationAudit audit, bool dispatch)
    {
        if (next.Tenant.TenantId != request.TenantId || next.Tenant.Revision != checked(revision + 1) || audit.TenantId != request.TenantId
            || audit.ActorPrincipalId != request.ExecutorPrincipalId || audit.Target != request.ResourceId || audit.Action != (dispatch ? DispatchAction : Action))
            throw new ArgumentException("Invalid Browser effect transaction.");
    }
    public static void Outcome(TenantOperationOutcome outcome, string reason, bool recovery)
    {
        if (recovery ? outcome != TenantOperationOutcome.Indeterminate || reason != Uncertain
            : outcome switch { TenantOperationOutcome.Completed => reason != Completed,
                TenantOperationOutcome.Denied => !Denials.Contains(reason), TenantOperationOutcome.Indeterminate => reason != Uncertain, _ => true })
            throw new ArgumentException("Invalid factual Browser effect outcome.");
    }
    public static AgentRun SettleRun(AgentRun run, TenantBrowserEffectFence fence, TenantOperationOutcome outcome, DateTimeOffset at)
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
    public static TenantOperationReceipt Matching(TenantOperationReceipt receipt, TenantBrowserEffectFence fence)
    {
        if (receipt.TenantId != fence.TenantId || receipt.RequestId != fence.RequestId || receipt.OperationId != fence.OperationId
            || receipt.RunId != fence.RunId || receipt.ActionHash != fence.ActionHash) throw new SecurityConflictException("operation_mismatch");
        return receipt;
    }
}
