using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantAccountActionRules
{
    public static EmailDraftSnapshot DraftSnapshot(PreparedAccountAction action)
    {
        var value = action.Draft ?? throw new ArgumentException("Exact draft evidence is required.");
        using var json = System.Text.Json.JsonDocument.Parse(value.RecipientsJson); var root = json.RootElement;
        string[] Addresses(string key) => System.Text.Json.JsonSerializer.Deserialize<string[]>(root.GetProperty(key)) ?? throw new ArgumentException("Exact recipients are required.");
        return new(value.DraftId, Addresses("to"), Addresses("cc"), Addresses("bcc"), value.Subject, value.Body,
            action.Credential.CredentialId, action.Credential.Reference, value.Sender, value.Revision,
            action.Credential.CredentialRevision, action.Credential.BindingId, action.Credential.BindingRevision, action.Credential.AuthEpoch);
    }
    public static void RequireDraft(PreparedAccountAction action, Guid instance, StoredEmailDraft? stored)
    {
        if (stored?.AgentInstanceId != instance || stored.SessionId != action.SessionId
            || System.Text.Json.JsonSerializer.Serialize(stored.Draft) != System.Text.Json.JsonSerializer.Serialize(DraftSnapshot(action)))
            throw new AccessDeniedException("account_draft_unavailable");
    }
    public static TenantAccountActionArguments Arguments(PreparedAccountAction action) => TenantAccountActionContract.Parse(action.SessionId, action.ToolName, action.ArgumentsJson);
    public static IEnumerable<(string Permission, AccessScope Scope)> Targets(Guid tenant, Guid instance, PreparedAccountAction action)
    {
        yield return ("sessions.read", TenantSessionRules.Scope(tenant, action.SessionId));
        var account = TenantCredentialRules.Scope(tenant, action.Credential.CredentialId);
        yield return ("credentials.read", account); yield return ("credentials.use", account);
        if (action.Purpose == CredentialPurpose.GoogleDriveAccount) yield return ("drive.read", account);
        var args = Arguments(action);
        if (args.Path is { } path)
        {
            if (path.StartsWith("/home/", StringComparison.Ordinal)) yield return ("agents.read", AccessScope.ForResource(tenant, "agent", instance.ToString("D")));
            yield return (args.Upload ? "workspaces.read" : "workspaces.write", TenantWorkspaceRules.Scope(new(tenant, instance,
                path.StartsWith("/working/", StringComparison.Ordinal) ? action.SessionId : null)));
        }
        if (!args.Download) yield return (action.Permission, account);
    }
    public static bool Primary(TenantApprovalRequest request, string permission, AccessScope scope) => permission == request.Permission
        && scope == TenantApprovalRules.Scope(request.TenantId, request.Operation);
    public static async ValueTask PermissionsAsync(TenantSecurityState state, Guid instance, PreparedAccountAction action, Guid principal,
        IamAuthorizer iam, bool primary, CancellationToken ct)
    {
        var args = Arguments(action);
        var primaryScope = args.Download ? TenantWorkspaceRules.Scope(new(state.Tenant.TenantId, instance,
            args.Path!.StartsWith("/working/", StringComparison.Ordinal) ? action.SessionId : null)) : TenantCredentialRules.Scope(state.Tenant.TenantId, action.Credential.CredentialId);
        foreach (var (permission, scope) in Targets(state.Tenant.TenantId, instance, action).Distinct())
        {
            if (!primary && permission == action.Permission && scope == primaryScope) continue;
            if (!(await iam.CheckOwnedTenantAsync(state, principal, PermissionCatalog.Get(permission), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("account_action_required");
        }
    }
    public static async ValueTask<bool> IndependentAsync(TenantSecurityState state, TenantApprovalRequest request, IamAuthorizer iam, CancellationToken ct)
    {
        foreach (var (permission, scope) in Targets(request.TenantId, request.Operation.InstanceId, request.Operation.AccountAction!).Distinct())
            if (TenantOperationPolicyService.Effective(state, PermissionCatalog.Get(permission)).IndependentReview
                && (Primary(request, permission, scope) || await TenantSandboxReadOperationRules.NeedsReviewAsync(state, request.OriginalInitiatorPrincipalId, permission, scope, iam, ct).ConfigureAwait(false))) return true;
        return false;
    }
    public static async ValueTask ReviewerAsync(TenantSecurityState state, TenantApprovalRequest request, Guid human, IamAuthorizer iam, CancellationToken ct)
    {
        if (!state.Users.Any(u => u.PrincipalId == human && u.Status == IdentityStatus.Active)) throw new AccessDeniedException("approver_unqualified");
        await PermissionsAsync(state, request.Operation.InstanceId, request.Operation.AccountAction!, human, iam, true, ct).ConfigureAwait(false);
        foreach (var (permission, scope) in Targets(request.TenantId, request.Operation.InstanceId, request.Operation.AccountAction!).Distinct())
        {
            if (!Primary(request, permission, scope) && !await TenantSandboxReadOperationRules.NeedsReviewAsync(state, request.OriginalInitiatorPrincipalId, permission, scope, iam, ct).ConfigureAwait(false)) continue;
            if (TenantOperationPolicyService.Effective(state, PermissionCatalog.Get(permission)).IndependentReview && request.OriginalInitiatorPrincipalId == human)
                throw new AccessDeniedException("independent_reviewer_required");
            if (!(await iam.CheckOwnedTenantAsync(state, human, PermissionCatalog.Get("approvals.approve"), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("approver_unqualified");
            if (Primary(request, permission, scope) && request.Kind == ApprovalKind.ExceptionalGrant
                && !(await iam.CheckOwnedTenantAsync(state, human, PermissionCatalog.Get("approvals.grant_exception"), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("approver_unqualified");
        }
    }
    public static void Require(PreparedAccountAction action, AgentRun run, TenantAgent agent, TenantSession session, AgentDefinition baseline,
        TenantCredentialBinding bound, bool exactSecret = true)
    {
        var current = InstanceSettingsResolver.Resolve(baseline, agent.Instance.SettingsOverrides); var args = Arguments(action);
        if (!TenantTaskExecutionContract.Allows(run, current) || !RolePermissions.AllowsTool(run.Admission.Configuration!.Definition, action.ToolName)
            || !RolePermissions.AllowsTool(current, action.ToolName) || action.SessionId != run.SessionId || args.Json != action.ArgumentsJson
            || action.Credential.Reference != args.Reference || args.Purpose != action.Purpose || args.Permission != action.Permission
            || session.Owner.TenantId != agent.TenantId || session.Owner.SessionId != run.SessionId
            || session.Owner.AgentInstanceId != run.AgentInstanceId || session.Owner.ExecutorPrincipalId != agent.PrincipalId
            || session.Snapshot.DurablyDeletedAt is not null || session.Snapshot.ArchivedAt is not null || session.Snapshot.LifecycleStatus != SessionLifecycleStatus.Active
            || session.Snapshot.Status is SessionStatus.Ending or SessionStatus.Ended || agent.Instance.Lifecycle != AgentInstanceLifecycle.Active
            || !TenantCredentialReadOperationRules.Eligible(run, agent, current, bound) || bound.Credential.Value.Purpose != action.Purpose
            || bound.Credential.Value.ProtectedPayload.Length == 0) throw new AccessDeniedException("account_source_changed");
        var actual = TenantCredentialOperationRules.Parameters(bound);
        if (exactSecret ? actual != action.Credential : actual with { SecretRevision = action.Credential.SecretRevision } != action.Credential)
            throw new SecurityConflictException("account_source_changed");
        if (action.Draft is { } draft && draft.Sender != bound.Credential.Value.EmailAccount?.EmailAddress) throw new SecurityConflictException("account_source_changed");
        if (args.Path is { } path)
        {
            var isolation = StateIsolationAccess.RestrictDefinition(run.Admission.Configuration.Definition, current).StateIsolationPolicy ?? StateIsolationPolicy.Default;
            TenantWorkspaceEffectRules.RequireHome(isolation, path, args.Download);
        }
    }
}
