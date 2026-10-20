using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantArtifactVerificationRules
{
    public const string Reason = "artifact_metadata_read_authorized";
    public const string AuditAction = "operations.artifacts.metadata.read";
    public static PreparedArtifactVerification Witness(ArtifactRecord item) => new(item.SessionId, item.ArtifactId, item.DisplayName, item.ContentType, item.ByteSize, item.Sha256Hex);
    public static void Require(PreparedArtifactVerification read, AgentRun run, TenantAgent agent, AgentDefinition current, TenantSession session, ArtifactRecord item)
    {
        if (!TenantTaskExecutionContract.Allows(run, current) || Witness(item) != read || item.TenantId != run.Owner.TenantId || read.SessionId != run.SessionId
            || session.Owner.TenantId != run.Owner.TenantId || session.Owner.SessionId != run.SessionId
            || session.Owner.AgentInstanceId != run.AgentInstanceId || session.Owner.ExecutorPrincipalId != run.Owner.ExecutorPrincipalId
            || agent.Instance.InstanceId != run.AgentInstanceId || agent.PrincipalId != run.Owner.ExecutorPrincipalId
            || !TenantArtifactVerificationContract.Allows(run.Admission.Configuration!.Definition) || !TenantArtifactVerificationContract.Allows(current)
            || session.Snapshot.DurablyDeletedAt is not null || session.Snapshot.ArchivedAt is not null
            || session.Snapshot.LifecycleStatus != SessionLifecycleStatus.Active || session.Snapshot.Status is SessionStatus.Ending or SessionStatus.Ended)
            throw new AccessDeniedException("artifact_source_changed");
    }
    public static IEnumerable<(string Permission, AccessScope Scope)> Targets(Guid tenant, PreparedArtifactVerification read) =>
        [("sessions.read", TenantSessionRules.Scope(tenant, read.SessionId)), ("artifacts.read", TenantArtifactRules.Scope(tenant, read.ArtifactId))];
    public static async ValueTask PermissionsAsync(TenantSecurityState state, PreparedArtifactVerification read, Guid principal, IamAuthorizer iam, CancellationToken ct)
    {
        foreach (var (permission, scope) in Targets(state.Tenant.TenantId, read))
            if (!(await iam.CheckOwnedTenantAsync(state, principal, PermissionCatalog.Get(permission), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("executor_read_required");
    }
    public static async ValueTask<bool> IndependentAsync(TenantSecurityState state, PreparedArtifactVerification read, Guid? requester, IamAuthorizer iam, CancellationToken ct)
    {
        foreach (var (permission, scope) in Targets(state.Tenant.TenantId, read))
            if (TenantOperationPolicyService.Effective(state, PermissionCatalog.Get(permission)).IndependentReview
                && (permission == "artifacts.read" || await TenantSandboxReadOperationRules.NeedsReviewAsync(state, requester, permission, scope, iam, ct).ConfigureAwait(false))) return true;
        return false;
    }
    public static async ValueTask ReviewerAsync(TenantSecurityState state, PreparedArtifactVerification read, Guid? requester, Guid human, IamAuthorizer iam, CancellationToken ct)
    {
        if (!state.Users.Any(u => u.PrincipalId == human && u.Status == IdentityStatus.Active)) throw new AccessDeniedException("approver_unqualified");
        await PermissionsAsync(state, read, human, iam, ct).ConfigureAwait(false);
        foreach (var (permission, scope) in Targets(state.Tenant.TenantId, read))
        {
            if (permission != "artifacts.read" && !await TenantSandboxReadOperationRules.NeedsReviewAsync(state, requester, permission, scope, iam, ct).ConfigureAwait(false)) continue;
            if (TenantOperationPolicyService.Effective(state, PermissionCatalog.Get(permission)).IndependentReview && requester == human)
                throw new AccessDeniedException("independent_reviewer_required");
            if (!(await iam.CheckOwnedTenantAsync(state, human, PermissionCatalog.Get("approvals.approve"), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("approver_unqualified");
        }
    }
    public static void ValidateEffect(TenantApprovalRequest request, TenantApprovalDecision decision, TenantOperationGrant? grant, AgentRun run, TenantOperationCommit commit, AgentRun? source)
    {
        TenantLocalEffectRules.ValidateApproval(request, decision, grant, run, commit, source);
        if (request.Operation.ArtifactVerification is null || request.Kind != ApprovalKind.Review || grant is not null
            || commit.Outcome is not (TenantOperationOutcome.Completed or TenantOperationOutcome.Denied)
            || commit.Outcome == TenantOperationOutcome.Completed && commit.ReasonCode != Reason
            || commit.ReasonCode.Length is < 1 or > 128 || commit.ReasonCode.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            throw new SecurityConflictException("operation_mismatch");
    }
}
