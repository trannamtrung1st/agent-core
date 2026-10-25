using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantAccountEffectRules
{
    public const string DispatchAction = "operations.accounts.action.dispatch";
    public const string ResultAction = "operations.accounts.action";
    public const string Completed = "account_action_completed";
    public const string Uncertain = "account_action_indeterminate";
    public const string NotApplied = "account_action_not_applied";
    private static readonly HashSet<string> Denials = new(StringComparer.Ordinal) { NotApplied, "account_draft_already_sent", "approval_expired", "grant_expired", "grant_unavailable",
        "grantor_authority_lost", "operation_authority_changed", "operation_witness_changed", "account_source_changed", "account_observation_unavailable",
        "account_action_required", "account_unavailable", "account_send_unavailable", "account_draft_unavailable", "executor_read_required", "executor_unavailable", "requester_unavailable", "tenant_unavailable", "resource_unavailable", "root_operation_conflict",
        "independent_reviewer_required", "approver_unqualified", "permission_denied", "capability_unavailable", "home_access_disabled", "home_read_only", "execution_claim_expired" };
    public static string Denial(string reason) => Denials.Contains(reason) ? reason : "account_source_changed";
    public static void Result(TenantApprovalRequest request, TenantOperationOutcome outcome, TenantAccountActionResult result)
    {
        var action = request.Operation.AccountAction ?? throw new ArgumentException("Account action result has no source.");
        var args = TenantAccountActionRules.Arguments(action);
        if (args.ToolName == Tools.ToolCatalog.DriveTrashBatch)
        {
            using var json = System.Text.Json.JsonDocument.Parse(args.Json);
            var files = Drive.DriveMutationArguments.Manifest(json.RootElement);
            if (result.DraftId is not null || result.FileId is not null || result.Path is not null || result.ByteSize is not null || result.Sha256 is not null
                || result.Items?.Count != files.Count || result.Items.Any(i => i is null) || !result.Items.Select(i => i.FileId).SequenceEqual(files.Select(f => f.FileId)))
                throw new ArgumentException("Invalid ordered batch result.");
            var preflightOnly = result.Items.All(i => i.Status is "denied" or "unattempted");
            var stopped = false;
            foreach (var item in result.Items)
            {
                if (item.Status is not ("completed" or "denied" or "indeterminate" or "unattempted") || !preflightOnly && stopped && item.Status != "unattempted")
                    throw new ArgumentException("Invalid batch completion prefix.");
                stopped |= item.Status != "completed";
            }
            if (result.Items.Count(i => i.Status == "denied") > 1) throw new ArgumentException("A batch stops at its first refusal.");
            if (outcome == TenantOperationOutcome.Completed ? stopped || result.Status != "completed" : !stopped || result.Status != "incomplete"
                || outcome == TenantOperationOutcome.Denied && result.Items.Any(i => i.Status is "completed" or "indeterminate"))
                throw new ArgumentException("Batch result contradicts its factual outcome.");
            return;
        }
        var alreadySent = args.ToolName == Tools.ToolCatalog.EmailSend && outcome == TenantOperationOutcome.Denied && result.Status == "already_sent";
        if (!alreadySent && (outcome != TenantOperationOutcome.Completed || result.Status != (args.ToolName == Tools.ToolCatalog.EmailCreateDraft ? "draft_created"
            : args.ToolName == Tools.ToolCatalog.EmailSend ? "sent" : args.Download ? "downloaded" : "completed"))) throw new ArgumentException("Invalid factual account result.");
        if (args.Purpose == Domain.Credentials.CredentialPurpose.EmailAccount)
        {
            if (result.DraftId != action.Draft!.DraftId || result.FileId is not null || result.Path is not null || result.ByteSize is not null
                || result.Sha256 is not null || result.Items is not null) throw new ArgumentException("Invalid draft/send result.");
            return;
        }
        if (result.DraftId is not null) throw new ArgumentException("Unexpected email result.");
        if (args.Download)
        {
            using var json = System.Text.Json.JsonDocument.Parse(args.Json);
            if (result.Path != args.Path || result.FileId != json.RootElement.GetProperty("fileId").GetString()
                || result.ByteSize is not (>= 0 and <= Drive.DriveService.MaxTransferBytes) || result.Sha256 is not { Length: 64 }
                || result.Sha256.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) || result.Items is not null)
                throw new ArgumentException("Invalid original-byte result.");
        }
        else
        {
            if (result.Path is not null || result.ByteSize is not null || result.Sha256 is not null) throw new ArgumentException("Unexpected transfer result.");
            if (result.FileId is null || Drive.DriveService.Id(result.FileId) != result.FileId || result.Items is not null) throw new ArgumentException("Invalid Drive result identity.");
            if (args.ToolName is not (Tools.ToolCatalog.DriveCreateFolder or Tools.ToolCatalog.DriveUpload))
            {
                using var json = System.Text.Json.JsonDocument.Parse(args.Json);
                if (result.FileId != json.RootElement.GetProperty("fileId").GetString()) throw new ArgumentException("Drive result target mismatch.");
            }
        }
    }
    private static DateTimeOffset Milliseconds(DateTimeOffset at) => DateTimeOffset.FromUnixTimeMilliseconds(at.ToUnixTimeMilliseconds());
    public static void Start(TenantApprovalRequest request, TenantApprovalDecision decision, TenantOperationGrant? grant, AgentRun run,
        TenantOperationCommit commit, AgentRun? source)
    {
        TenantLocalEffectRules.ValidateApproval(request, decision, grant, run, commit, source);
        RequireGrant(request, grant, OperationGrantStatus.Available);
        if (request.Operation.AccountAction is null || commit.Outcome is not (TenantOperationOutcome.Completed or TenantOperationOutcome.Denied)
            || (commit.Outcome == TenantOperationOutcome.Completed ? commit.ReasonCode != "operation_completed" : !Denials.Contains(commit.ReasonCode)))
            throw new SecurityConflictException("operation_mismatch");
    }
    public static void RequireGrant(TenantApprovalRequest request, TenantOperationGrant? grant, OperationGrantStatus status)
    {
        if (!Enum.IsDefined(request.Kind) || request.Operation.AccountAction is null) throw new SecurityConflictException("grant_mismatch");
        if (request.Kind != ApprovalKind.ExceptionalGrant) { if (grant is not null) throw new SecurityConflictException("grant_mismatch"); return; }
        var permission = PermissionCatalog.Get(request.Permission);
        if (!permission.Delegatable || permission.HumanOnly || permission.SystemOnly || request.Permission is not ("email.draft" or "email.send" or "drive.write" or "drive.delete" or "workspaces.write")
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
        await TenantAccountActionRules.PermissionsAsync(state, request.Operation.InstanceId, request.Operation.AccountAction!, TenantWorkspaceRules.Context(request.Operation), request.ExecutorPrincipalId, iam, request.Kind == ApprovalKind.Review, ct).ConfigureAwait(false);
        try { await TenantAccountActionRules.ReviewerAsync(state, request, decision.HumanPrincipalId, iam, ct).ConfigureAwait(false); }
        catch (AccessDeniedException) when (request.Kind == ApprovalKind.ExceptionalGrant) { throw new AccessDeniedException("grantor_authority_lost"); }
    }
    public static TenantAccountDispatch Fence(TenantApprovalRequest request, AgentRun run) => new(request.TenantId, request.RequestId,
        request.Operation.OperationId, run.AgentRunId, request.Operation.ActionHash, run.SideEffect.ExternalEffect ?? throw new SecurityConflictException("account_effect_fence_required"));
    public static void RequireFence(TenantAccountDispatch fence, TenantApprovalRequest request, AgentRun run, AgentRun? source, TenantOperationGrant? grant)
    {
        TenantRunContinuationRules.Require(run, request, source);
        if (request.Operation.AccountAction is null || request.Status != TenantApprovalStatus.Approved || fence != Fence(request, run)
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
            || audit.Action != (dispatch ? DispatchAction : ResultAction)) throw new ArgumentException("Invalid account effect transaction.");
    }
    public static void Outcome(TenantOperationOutcome outcome, string reason, bool recovery)
    {
        if (recovery ? outcome != TenantOperationOutcome.Indeterminate || reason != Uncertain : outcome switch {
            TenantOperationOutcome.Completed => reason != Completed, TenantOperationOutcome.Denied => !Denials.Contains(reason),
            TenantOperationOutcome.Indeterminate => reason != Uncertain, _ => true }) throw new ArgumentException("Invalid factual account outcome.");
    }
    public static AgentRun SettleRun(AgentRun run, TenantAccountDispatch fence, TenantOperationOutcome outcome, DateTimeOffset at)
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
    public static TenantOperationReceipt Matching(TenantOperationReceipt receipt, TenantAccountDispatch fence)
    {
        if (receipt.TenantId != fence.TenantId || receipt.RequestId != fence.RequestId || receipt.OperationId != fence.OperationId || receipt.RunId != fence.RunId
            || receipt.ActionHash != fence.ActionHash || receipt.SourceCallId is not null || receipt.ParametersHash is not null) throw new SecurityConflictException("operation_mismatch");
        return receipt;
    }
}
