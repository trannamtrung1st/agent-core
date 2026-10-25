using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantArtifactReviewRules
{
    public static void RequireTask(AgentRun run)
    {
        if (!TenantTaskExecutionContract.Allows(run, run.Admission.Configuration!.Definition))
            throw new AccessDeniedException("artifact_execution_unavailable");
    }
    public static TenantArtifactPublicationArguments Require(TenantApprovalRequest request, AgentRun run, TenantAgent agent,
        AgentDefinition current, TenantSession session, AgentRun? source = null)
    {
        TenantApprovalContinuationRules.Require(request, run, source);
        RequireTask(request.RunId == run.AgentRunId ? run : source ?? throw new AccessDeniedException("artifact_direct_chat_required"));
        var value = request.Operation.ArtifactPublication ?? throw new SecurityConflictException("operation_mismatch");
        if (value.SessionId != request.SessionId || value.SessionId != run.SessionId || session.Owner.TenantId != request.TenantId
            || session.Owner.SessionId != value.SessionId || session.Owner.AgentInstanceId != run.AgentInstanceId || session.Owner.ExecutorPrincipalId != request.ExecutorPrincipalId
            || agent.TenantId != request.TenantId || agent.Instance.InstanceId != run.AgentInstanceId || agent.PrincipalId != request.ExecutorPrincipalId)
            throw new SecurityConflictException("operation_mismatch");
        if (!TenantTaskExecutionContract.Allows(run, current) || !TenantArtifactToolContract.AllowsPublication(run.Admission.Configuration!.Definition, value) || !TenantArtifactToolContract.AllowsPublication(current, value)) throw new AccessDeniedException("capability_unavailable");
        var args = TenantArtifactToolContract.ParsePublication(value);
        if (args.SourcePath is { } path) TenantArtifactSourceRules.Require(run, agent, current, session, path, value.Source);
        return args;
    }
    public static async ValueTask ExecutorAsync(TenantSecurityState state, TenantApprovalRequest request, IamAuthorizer iam, CancellationToken ct)
    {
        var permission = PermissionCatalog.Get("artifacts.create");
        if (permission.HumanOnly || permission.SystemOnly || request.Kind == ApprovalKind.ExceptionalGrant && !permission.Delegatable) throw new AccessDeniedException("operation_authority_changed");
        var scope = TenantSessionRules.Scope(request.TenantId, request.SessionId);
        if (!(await iam.CheckOwnedTenantAsync(state, request.ExecutorPrincipalId, PermissionCatalog.Get("sessions.read"), scope, ct).ConfigureAwait(false)).Allowed)
            throw new AccessDeniedException("executor_read_required");
        foreach (var (key, target) in SourceTargets(request))
            if (!(await iam.CheckOwnedTenantAsync(state, request.ExecutorPrincipalId, PermissionCatalog.Get(key), target, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("executor_read_required");
        if (request.Kind == ApprovalKind.Review && !(await iam.CheckOwnedTenantAsync(state, request.ExecutorPrincipalId, PermissionCatalog.Get("artifacts.create"), scope, ct).ConfigureAwait(false)).Allowed)
            throw new AccessDeniedException("operation_authority_changed");
    }
    public static async ValueTask ReadAsync(TenantSecurityState state, TenantApprovalRequest request, Guid human, IamAuthorizer iam, CancellationToken ct, bool ownedSources = false)
    {
        if (!(await iam.CheckTenantAsync(state, human, PermissionCatalog.Get("sessions.read"), TenantSessionRules.Scope(request.TenantId, request.SessionId), ct).ConfigureAwait(false)).Allowed)
            throw new AccessDeniedException("approval_unavailable");
        foreach (var (key, scope) in SourceTargets(request))
            if (!(await (ownedSources ? iam.CheckOwnedTenantAsync(state, human, PermissionCatalog.Get(key), scope, ct) : iam.CheckTenantAsync(state, human, PermissionCatalog.Get(key), scope, ct)).ConfigureAwait(false)).Allowed) throw new AccessDeniedException("approval_unavailable");
    }
    public static async ValueTask ReviewerAsync(TenantSecurityState state, TenantApprovalRequest request, Guid human, IamAuthorizer iam, CancellationToken ct, bool ownedSources = false)
    {
        await ReadAsync(state, request, human, iam, ct, ownedSources).ConfigureAwait(false);
        if (!state.Users.Any(u => u.PrincipalId == human && u.Status == IdentityStatus.Active)
            || TenantOperationPolicyService.Effective(state, PermissionCatalog.Get("artifacts.create")).IndependentReview && human == request.OriginalInitiatorPrincipalId)
            throw new AccessDeniedException("approver_unqualified");
        var target = TenantSessionRules.Scope(request.TenantId, request.SessionId);
        foreach (var key in request.Kind == ApprovalKind.ExceptionalGrant ? new[] { "artifacts.create", "approvals.approve", "approvals.grant_exception" } : ["artifacts.create", "approvals.approve"])
            if (!(await iam.CheckOwnedTenantAsync(state, human, PermissionCatalog.Get(key), target, ct).ConfigureAwait(false)).Allowed) throw new AccessDeniedException("approver_unqualified");
        foreach (var (key, scope) in SourceTargets(request))
        {
            if (!await TenantArtifactSourceRules.NeedsReviewAsync(state, request.OriginalInitiatorPrincipalId, key, scope, iam, ct, ownedSources).ConfigureAwait(false)) continue;
            if (TenantOperationPolicyService.Effective(state, PermissionCatalog.Get(key)).IndependentReview && human == request.OriginalInitiatorPrincipalId)
                throw new AccessDeniedException("independent_reviewer_required");
            if (!(await iam.CheckOwnedTenantAsync(state, human, PermissionCatalog.Get("approvals.approve"), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("approver_unqualified");
        }
    }
    public static IEnumerable<(string Permission, AccessScope Scope)> SourceTargets(TenantApprovalRequest request) => request.Operation.ArtifactPublication?.Source is { } source
        ? TenantArtifactSourceRules.Targets(request.TenantId, request.Operation.InstanceId, request.SessionId, source.Path, TenantWorkspaceRules.Context(request.Operation), source.ResourceKey, source.AttachmentId) : [];
    public static async ValueTask<bool> IndependentAsync(TenantSecurityState state, TenantApprovalRequest request, IamAuthorizer iam, CancellationToken ct)
    {
        if (TenantOperationPolicyService.Effective(state, PermissionCatalog.Get("artifacts.create")).IndependentReview) return true;
        foreach (var (key, scope) in SourceTargets(request))
            if (TenantOperationPolicyService.Effective(state, PermissionCatalog.Get(key)).IndependentReview
                && await TenantSandboxReadOperationRules.NeedsReviewAsync(state, request.OriginalInitiatorPrincipalId, key, scope, iam, ct).ConfigureAwait(false)) return true;
        return false;
    }
    public static void ValidateEffect(TenantApprovalRequest request, TenantApprovalDecision decision, TenantOperationGrant? grant,
        AgentRun run, TenantOperationCommit commit, AgentRun? source)
    {
        TenantLocalEffectRules.ValidateApproval(request, decision, grant, run, commit, source);
        if (commit.ArtifactSourceContent is not null && request.Operation.ArtifactPublication?.Source is null)
            throw new SecurityConflictException("operation_mismatch");
        if (request.Operation.ArtifactPublication is null || commit.Outcome is not (TenantOperationOutcome.Completed or TenantOperationOutcome.Denied)
            || commit.Outcome == TenantOperationOutcome.Completed && commit.ReasonCode != "operation_completed"
            || commit.Outcome == TenantOperationOutcome.Denied && !Denials.Contains(commit.ReasonCode)) throw new SecurityConflictException("operation_mismatch");
    }
    private static readonly HashSet<string> Denials = new(StringComparer.Ordinal) { "approval_expired", "grant_expired", "grantor_authority_lost",
        "operation_authority_changed", "operation_witness_changed", "requester_unavailable", "root_operation_conflict", "executor_read_required",
        "capability_unavailable", "resource_unavailable", "artifact_direct_chat_required", "artifact_execution_unavailable", "approver_unqualified", "approval_unavailable", "tenant_effect_unavailable", "artifact_publication_mismatch", "artifact_content_unavailable",
        "workspace_source_changed", "attachment_source_changed", "attachment_content_unavailable", "resource_source_changed", "resource_content_unavailable", "home_access_disabled", "independent_reviewer_required" };
}
