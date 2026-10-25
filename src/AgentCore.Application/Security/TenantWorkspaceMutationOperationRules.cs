using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantWorkspaceMutationOperationRules
{
    // Correlation only: the fixed witness/revisions cannot authorize or validate a source.
    public static PreparedTenantOperation Correlation(AgentRun run, TenantWorkspaceMutationArguments args) => TenantApprovalRules.ForRun(TenantApprovalRules.PrepareWorkspaceMutation(
        run.AgentRunId, run.AgentInstanceId, TenantApprovalRules.MutationParameters(run.SessionId, args, new string('0', 64)), 1, 1), run);

    public static TenantWorkspaceMutationArguments Arguments(PreparedWorkspaceMutation value) => TenantWorkspaceMutationContract.Parse(value.SessionId, value.ToolName, value.ArgumentsJson);
    public static IEnumerable<(string Permission, AccessScope Scope)> Targets(Guid tenant, Guid instance, PreparedWorkspaceMutation value, AgentCore.Domain.Spaces.RunSpaceWitness context)
    {
        yield return ("sessions.read", TenantSessionRules.Scope(tenant, value.SessionId));
        var scopes = TenantApprovalRules.MutationScopes(Arguments(value));
        foreach (var target in scopes)
        {
            var owner = TenantWorkspaceRules.ForCaptured(tenant, value.SessionId, target.Root, context);
            yield return TenantWorkspaceMutationRules.Parent(owner);
            yield return (target.Permission, TenantWorkspaceRules.Scope(owner));
        }
    }
    public static bool Primary(TenantApprovalRequest request, string permission, AccessScope scope) => permission == request.Permission
        && scope == AccessScope.ForResource(request.TenantId, request.ResourceType, request.ResourceId);
    public static void Require(TenantApprovalRequest request, AgentRun run, TenantAgent agent, AgentDefinition current, TenantSession session, AgentRun? source = null)
    {
        TenantApprovalContinuationRules.Require(request, run, source);
        var value = request.Operation.WorkspaceMutation ?? throw new SecurityConflictException("operation_mismatch");
        var args = Arguments(value); TenantWorkspaceEffectRules.Session(run, agent, session);
        if (value.SessionId != run.SessionId || agent.TenantId != request.TenantId || agent.PrincipalId != request.ExecutorPrincipalId
            || agent.Instance.InstanceId != request.Operation.InstanceId || run.Owner.ExecutorPrincipalId != agent.PrincipalId
            || !TenantTaskExecutionContract.Allows(run, current)
            || !TenantWorkspaceMutationContract.Allows(run.Admission.Configuration!.Definition, value.ToolName)
            || !TenantWorkspaceMutationContract.Allows(current, value.ToolName)) throw new AccessDeniedException("capability_unavailable");
        var isolation = StateIsolationAccess.RestrictDefinition(run.Admission.Configuration.Definition, current).StateIsolationPolicy ?? StateIsolationPolicy.Default;
        foreach (var (path, permission) in args.Targets) TenantWorkspaceEffectRules.RequireHome(isolation, path, permission != "workspaces.read");
    }
    public static async ValueTask ExecutorAsync(TenantSecurityState state, TenantApprovalRequest request, IamAuthorizer iam, CancellationToken ct)
    {
        foreach (var (key, scope) in Targets(request.TenantId, request.Operation.InstanceId, request.Operation.WorkspaceMutation!, TenantWorkspaceRules.Context(request.Operation)))
        {
            var permission = PermissionCatalog.Get(key);
            if (permission.SystemOnly || permission.HumanOnly || Primary(request, key, scope) && request.Kind == ApprovalKind.ExceptionalGrant && !permission.Delegatable)
                throw new AccessDeniedException("operation_authority_changed");
            if (Primary(request, key, scope) && request.Kind == ApprovalKind.ExceptionalGrant) continue;
            if (!(await iam.CheckOwnedTenantAsync(state, request.ExecutorPrincipalId, permission, scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException(key.EndsWith(".read", StringComparison.Ordinal) ? "executor_read_required" : "operation_authority_changed");
        }
    }
    public static async ValueTask ReadAsync(TenantSecurityState state, TenantApprovalRequest request, Guid human, IamAuthorizer iam, CancellationToken ct)
    {
        foreach (var (key, scope) in Targets(request.TenantId, request.Operation.InstanceId, request.Operation.WorkspaceMutation!, TenantWorkspaceRules.Context(request.Operation)))
            if (!(await iam.CheckOwnedTenantAsync(state, human, PermissionCatalog.Get(key), scope, ct).ConfigureAwait(false)).Allowed) throw new AccessDeniedException("approval_unavailable");
    }
    public static async ValueTask<bool> IndependentAsync(TenantSecurityState state, TenantApprovalRequest request, IamAuthorizer iam, CancellationToken ct)
    {
        foreach (var (key, scope) in Targets(request.TenantId, request.Operation.InstanceId, request.Operation.WorkspaceMutation!, TenantWorkspaceRules.Context(request.Operation)))
            if (TenantOperationPolicyService.Effective(state, PermissionCatalog.Get(key)).IndependentReview
                && (Primary(request, key, scope) || await TenantSandboxReadOperationRules.NeedsReviewAsync(state, request.OriginalInitiatorPrincipalId, key, scope, iam, ct).ConfigureAwait(false))) return true;
        return false;
    }
    public static async ValueTask ReviewerAsync(TenantSecurityState state, TenantApprovalRequest request, Guid human, IamAuthorizer iam, CancellationToken ct)
    {
        if (!state.Users.Any(u => u.PrincipalId == human && u.Status == IdentityStatus.Active)) throw new AccessDeniedException("approver_unqualified");
        await ReadAsync(state, request, human, iam, ct).ConfigureAwait(false);
        foreach (var (key, scope) in Targets(request.TenantId, request.Operation.InstanceId, request.Operation.WorkspaceMutation!, TenantWorkspaceRules.Context(request.Operation)))
        {
            if (!Primary(request, key, scope) && !await TenantSandboxReadOperationRules.NeedsReviewAsync(state, request.OriginalInitiatorPrincipalId, key, scope, iam, ct).ConfigureAwait(false)) continue;
            if (TenantOperationPolicyService.Effective(state, PermissionCatalog.Get(key)).IndependentReview && human == request.OriginalInitiatorPrincipalId)
                throw new AccessDeniedException("independent_reviewer_required");
            if (!(await iam.CheckOwnedTenantAsync(state, human, PermissionCatalog.Get("approvals.approve"), scope, ct).ConfigureAwait(false)).Allowed) throw new AccessDeniedException("approver_unqualified");
            if (Primary(request, key, scope) && request.Kind == ApprovalKind.ExceptionalGrant
                && !(await iam.CheckOwnedTenantAsync(state, human, PermissionCatalog.Get("approvals.grant_exception"), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("approver_unqualified");
        }
    }
}
