using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantCredentialReadOperationRules
{
    public const string Reason = "credential_metadata_read_authorized";
    public const string AuditAction = "operations.credentials.metadata.read";
    public static CredentialReadReference Reference(TenantCredentialBinding bound) => new(bound.Credential.Value.CredentialId,
        bound.Binding.BindingId, bound.Binding.Reference, bound.Binding.Revision, bound.Credential.Value.Revision,
        bound.Credential.Value.DisplayName, bound.Credential.Value.Kind.ToString());
    public static bool Eligible(AgentRun run, TenantAgent agent, AgentDefinition current, TenantCredentialBinding bound)
    {
        var b = bound.Binding; var c = bound.Credential;
        bool Allows(AgentDefinition definition) => c.Value.Purpose == CredentialPurpose.General
            ? c.Value.Kind == CredentialKind.Password && RolePermissions.AllowsTool(definition, ToolCatalog.BrowserFillCredential)
            : TenantCredentialOperationRules.Allows(definition, c.Value.Purpose);
        return c.Owner.TenantId == run.Owner.TenantId && c.Owner.DeletedAt is null && c.Owner.CredentialId == c.Value.CredentialId
            && b.CredentialId == c.Value.CredentialId && b.AgentInstanceId == agent.Instance.InstanceId && agent.Instance.InstanceId == run.AgentInstanceId
            && agent.PrincipalId == run.Owner.ExecutorPrincipalId && c.Value.Status == CredentialStatus.Active
            && run.Admission.Configuration!.CredentialBindings.Any(pin => pin == b) && Allows(run.Admission.Configuration.Definition) && Allows(current);
    }
    public static void Require(PreparedCredentialRead read, AgentRun run, TenantAgent agent, TenantSession session, AgentDefinition baseline,
        IReadOnlyList<TenantCredentialBinding> bindings)
    {
        if (run.Owner.TenantId != session.Owner.TenantId || session.Owner.SessionId != run.SessionId || read.SessionId != run.SessionId
            || session.Owner.AgentInstanceId != run.AgentInstanceId || session.Owner.ExecutorPrincipalId != run.Owner.ExecutorPrincipalId
            || session.Snapshot.DurablyDeletedAt is not null || session.Snapshot.ArchivedAt is not null
            || session.Snapshot.LifecycleStatus != SessionLifecycleStatus.Active || session.Snapshot.Status is SessionStatus.Ending or SessionStatus.Ended)
            throw new AccessDeniedException("credential_source_changed");
        var current = InstanceSettingsResolver.ResolveForAgent(baseline, agent);
        var family = TenantCredentialDiscoveryContract.Parse(read.ArgumentsJson).Family;
        if (family is not null && (!TenantTaskExecutionContract.Allows(run, current) || !TenantCredentialDiscoveryContract.Allows(current, family) || !TenantCredentialDiscoveryContract.Allows(run.Admission.Configuration!.Definition, family))) throw new AccessDeniedException("capability_unavailable");
        foreach (var reference in TenantApprovalRules.CredentialReadReferences(read))
        {
            var bound = bindings.SingleOrDefault(b => b.Binding.BindingId == reference.BindingId);
            if (bound is null || Reference(bound) != reference || !Eligible(run, agent, current, bound) || !TenantCredentialDiscoveryContract.Matches(family, bound.Credential.Value.Purpose))
                throw new SecurityConflictException("credential_source_changed");
        }
    }
    public static IEnumerable<(string Permission, AccessScope Scope)> Targets(Guid tenant, PreparedCredentialRead read)
    {
        yield return ("sessions.read", TenantSessionRules.Scope(tenant, read.SessionId));
        var family = TenantCredentialDiscoveryContract.Parse(read.ArgumentsJson).Family;
        foreach (var id in TenantApprovalRules.CredentialReadReferences(read).Select(r => r.CredentialId).Distinct())
        {
            var scope = TenantCredentialRules.Scope(tenant, id);
            yield return ("credentials.read", scope);
            if (family is not null) yield return (family + ".read", scope);
        }
    }
    public static async ValueTask PermissionsAsync(TenantSecurityState state, PreparedCredentialRead read, Guid principal, IamAuthorizer iam, CancellationToken ct)
    {
        foreach (var (permission, scope) in Targets(state.Tenant.TenantId, read))
            if (!(await iam.CheckTenantAsync(state, principal, PermissionCatalog.Get(permission), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("permission_denied");
    }
    public static async ValueTask<bool> IndependentAsync(TenantSecurityState state, PreparedCredentialRead read, Guid? requester, IamAuthorizer iam, CancellationToken ct)
    {
        foreach (var (permission, scope) in Targets(state.Tenant.TenantId, read))
            if (TenantOperationPolicyService.Effective(state, PermissionCatalog.Get(permission)).IndependentReview
                && (permission == "credentials.read" || await TenantSandboxReadOperationRules.NeedsReviewAsync(state, requester, permission, scope, iam, ct).ConfigureAwait(false))) return true;
        return false;
    }
    public static async ValueTask ReviewerAsync(TenantSecurityState state, PreparedCredentialRead read, Guid? requester, Guid human, IamAuthorizer iam, CancellationToken ct)
    {
        if (!state.Users.Any(u => u.PrincipalId == human && u.Status == IdentityStatus.Active)) throw new AccessDeniedException("approver_unqualified");
        await PermissionsAsync(state, read, human, iam, ct).ConfigureAwait(false);
        foreach (var (permission, scope) in Targets(state.Tenant.TenantId, read))
        {
            if (permission != "credentials.read" && !await TenantSandboxReadOperationRules.NeedsReviewAsync(state, requester, permission, scope, iam, ct).ConfigureAwait(false)) continue;
            if (TenantOperationPolicyService.Effective(state, PermissionCatalog.Get(permission)).IndependentReview && requester == human)
                throw new AccessDeniedException("independent_reviewer_required");
            if (!(await iam.CheckTenantAsync(state, human, PermissionCatalog.Get("approvals.approve"), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("approver_unqualified");
        }
    }
    public static void ValidateEffect(TenantApprovalRequest request, TenantApprovalDecision decision, TenantOperationGrant? grant, AgentRun run,
        TenantOperationCommit commit, AgentRun? source)
    {
        TenantLocalEffectRules.ValidateApproval(request, decision, grant, run, commit, source);
        if (request.Operation.CredentialRead is null || request.Kind != ApprovalKind.Review || grant is not null
            || commit.Outcome is not (TenantOperationOutcome.Completed or TenantOperationOutcome.Denied)
            || commit.Outcome == TenantOperationOutcome.Completed && commit.ReasonCode != Reason
            || commit.ReasonCode.Length is < 1 or > 128 || commit.ReasonCode.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            throw new SecurityConflictException("operation_mismatch");
    }
}
