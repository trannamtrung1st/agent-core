using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantAccountReadRules
{
    public static TenantApprovalSnapshot Snapshot(TenantAgent agent, TenantCredentialBinding bound) => new(agent.Instance.Persona.Name,
        Credential: new(bound.Credential.Value.DisplayName, bound.Credential.Value.Purpose.ToString(),
            bound.Credential.Value.EmailAccount?.AuthMethod == EmailAuthMethod.Password ? "Configured" : "Connected"));
    public const string Reason = "account_read_authorized";
    public const string AuditAction = "operations.accounts.read.authorize";
    public static string Family(PreparedAccountRead read) => read.Purpose == CredentialPurpose.EmailAccount ? "email.read" : "drive.read";
    public static PreparedCredentialConnectionCheck Witness(TenantCredentialBinding bound) => TenantCredentialOperationRules.Parameters(bound);
    public static IEnumerable<(string Permission, AccessScope Scope)> Targets(Guid tenant, PreparedAccountRead read, bool use = true)
    {
        yield return ("sessions.read", TenantSessionRules.Scope(tenant, read.SessionId));
        if (read.Source is { } source)
        {
            yield return ("sessions.read", TenantSessionRules.Scope(tenant, source.SessionId));
            yield return ("agent-runs.read", TenantRunRules.Scope(tenant, source.RunId));
        }
        var scope = TenantCredentialRules.Scope(tenant, read.Credential.CredentialId);
        yield return ("credentials.read", scope); yield return (Family(read), scope);
        if (use) yield return ("credentials.use", scope);
    }
    public static async ValueTask PermissionsAsync(TenantSecurityState state, PreparedAccountRead read, Guid principal, IamAuthorizer iam, bool use, CancellationToken ct)
    {
        foreach (var (permission, scope) in Targets(state.Tenant.TenantId, read, use))
            if (!(await iam.CheckTenantAsync(state, principal, PermissionCatalog.Get(permission), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("account_read_required");
    }
    public static async ValueTask<bool> IndependentAsync(TenantSecurityState state, PreparedAccountRead read, Guid? requester, IamAuthorizer iam, CancellationToken ct)
    {
        foreach (var (permission, scope) in Targets(state.Tenant.TenantId, read))
            if (TenantOperationPolicyService.Effective(state, PermissionCatalog.Get(permission)).IndependentReview
                && (permission == "credentials.use" || await TenantSandboxReadOperationRules.NeedsReviewAsync(state, requester, permission, scope, iam, ct).ConfigureAwait(false))) return true;
        return false;
    }
    public static async ValueTask ReviewerAsync(TenantSecurityState state, TenantApprovalRequest request, Guid human, IamAuthorizer iam, CancellationToken ct)
    {
        var read = request.Operation.AccountRead ?? throw new AccessDeniedException("account_read_unavailable");
        if (!state.Users.Any(u => u.PrincipalId == human && u.Status == IdentityStatus.Active)) throw new AccessDeniedException("approver_unqualified");
        await PermissionsAsync(state, read, human, iam, true, ct).ConfigureAwait(false);
        foreach (var (permission, scope) in Targets(state.Tenant.TenantId, read))
        {
            if (permission != "credentials.use" && !await TenantSandboxReadOperationRules.NeedsReviewAsync(state, request.OriginalInitiatorPrincipalId, permission, scope, iam, ct).ConfigureAwait(false)) continue;
            if (TenantOperationPolicyService.Effective(state, PermissionCatalog.Get(permission)).IndependentReview && request.OriginalInitiatorPrincipalId == human)
                throw new AccessDeniedException("independent_reviewer_required");
            if (!(await iam.CheckTenantAsync(state, human, PermissionCatalog.Get("approvals.approve"), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("approver_unqualified");
            if (permission == "credentials.use" && request.Kind == ApprovalKind.ExceptionalGrant
                && !(await iam.CheckTenantAsync(state, human, PermissionCatalog.Get("approvals.grant_exception"), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("approver_unqualified");
        }
    }
    public static void Require(PreparedAccountRead read, AgentRun run, TenantAgent agent, TenantSession session, AgentDefinition baseline,
        TenantCredentialBinding bound, bool exactSecret = true)
    {
        var current = InstanceSettingsResolver.ResolveForAgent(baseline, agent);
        var args = TenantAccountReadContract.Parse(read.ToolName, read.ArgumentsJson);
        TenantAccountOperationReadRules.Validate(read);
        if (!TenantTaskExecutionContract.Allows(run, current) || !RolePermissions.AllowsTool(run.Admission.Configuration!.Definition, read.ToolName)
            || !RolePermissions.AllowsTool(current, read.ToolName) || read.SessionId != run.SessionId || read.Purpose != args.Purpose
            || args.Json != read.ArgumentsJson || args.Reference != read.Credential.Reference
            || session.Owner.TenantId != run.Owner.TenantId || session.Owner.AgentInstanceId != run.AgentInstanceId
            || session.Owner.ExecutorPrincipalId != run.Owner.ExecutorPrincipalId || session.Owner.SessionId != run.SessionId
            || session.Snapshot.DurablyDeletedAt is not null || session.Snapshot.ArchivedAt is not null
            || session.Snapshot.LifecycleStatus != SessionLifecycleStatus.Active || session.Snapshot.Status is SessionStatus.Ending or SessionStatus.Ended
            || agent.Instance.Lifecycle != AgentInstanceLifecycle.Active || !TenantCredentialReadOperationRules.Eligible(run, agent, current, bound)
            || bound.Credential.Value.Purpose != read.Purpose || bound.Credential.Value.ProtectedPayload.Length == 0)
            throw new AccessDeniedException("account_source_changed");
        var actual = Witness(bound);
        if (exactSecret ? actual != read.Credential : actual with { CredentialRevision = read.Credential.CredentialRevision, SecretRevision = read.Credential.SecretRevision } != read.Credential)
            throw new SecurityConflictException("account_source_changed");
    }
    public static void ValidateEffect(TenantApprovalRequest request, TenantApprovalDecision decision, TenantOperationGrant? grant, AgentRun run, TenantOperationCommit commit, AgentRun? source)
    {
        TenantLocalEffectRules.ValidateApproval(request, decision, grant, run, commit, source);
        if (request.Operation.AccountRead is null || commit.Outcome is not (TenantOperationOutcome.Completed or TenantOperationOutcome.Denied)
            || commit.Outcome == TenantOperationOutcome.Completed && commit.ReasonCode != Reason
            || commit.ReasonCode.Length is < 1 or > 128 || commit.ReasonCode.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            throw new SecurityConflictException("operation_mismatch");
    }
    public static void Receipt(TenantApprovalRequest request, AgentRun run, TenantOperationReceipt receipt, TenantApprovalDecision decision, TenantOperationGrant? grant)
    {
        if (request.Operation.AccountRead is null || request.Status != TenantApprovalStatus.Approved || request.SessionId != run.SessionId
            || request.TenantId != run.Owner.TenantId || request.ExecutorPrincipalId != run.Owner.ExecutorPrincipalId || request.Operation.InstanceId != run.AgentInstanceId
            || request.RootActivationId != run.Admission.ExecutionContext?.RootActivationId || request.OriginalInitiatorPrincipalId != run.Admission.ExecutionContext.OriginalInitiatorPrincipalId
            || receipt.Outcome != TenantOperationOutcome.Completed || receipt.ReasonCode != Reason || receipt.RequestId != request.RequestId
            || receipt.OperationId != request.Operation.OperationId || receipt.ActionHash != request.Operation.ActionHash || receipt.TenantId != request.TenantId
            || receipt.SessionId != request.SessionId || receipt.InstanceId != request.Operation.InstanceId || receipt.ExecutorPrincipalId != request.ExecutorPrincipalId
            || receipt.RootActivationId != request.RootActivationId || decision.RequestId != request.RequestId || decision.TenantId != request.TenantId
            || decision.Status != TenantApprovalStatus.Approved || decision.RequestRevision != request.Revision || decision.ActionHash != request.Operation.ActionHash
            || (request.Kind == ApprovalKind.Review ? grant is not null : grant is null || grant.Status != OperationGrantStatus.Consumed
                || grant.RequestId != request.RequestId || grant.TenantId != request.TenantId || grant.ExecutorPrincipalId != request.ExecutorPrincipalId
                || grant.OperationId != receipt.OperationId || grant.ActionHash != receipt.ActionHash)) throw new AccessDeniedException("account_read_unavailable");
    }
}
