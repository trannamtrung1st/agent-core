using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Application.Agents;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantWorkspaceReadOperationRules
{
    public const string Reason = "workspace_read_authorized";
    public const string AuditAction = "operations.workspace.read";
    public static IEnumerable<(string Permission, AccessScope Scope)> Targets(Guid tenant, Guid instance, PreparedWorkspaceRead value)
    {
        yield return ("sessions.read", TenantSessionRules.Scope(tenant, value.SessionId));
        if (!value.Working) yield return ("agents.read", AccessScope.ForResource(tenant, "agent", instance.ToString("D")));
        yield return value.ResourceKey is { } key ? ("resources.read", TenantAgentChildScopes.Scope(tenant, instance, "resource", key))
            : ("workspaces.read", TenantWorkspaceRules.Scope(new(tenant, instance, value.Working ? value.SessionId : null)));
    }
    public static void Require(PreparedWorkspaceRead value, AgentRun run, TenantAgent agent, AgentDefinition current, TenantSession session)
    {
        var args = TenantWorkspaceReadContract.Parse(value.ToolName, value.ArgumentsJson);
        var owner = new TenantWorkspaceReference(agent.TenantId, agent.Instance.InstanceId, value.Working ? value.SessionId : null);
        if (value.ResourceKey is null) TenantWorkspaceRules.Path(owner, args.Path, TenantWorkspaceReadContract.IsFile(value.ToolName));
        else
        {
            TenantResourceSourceTarget.ValidatePath(args.Path);
            if (!TenantWorkspaceReadContract.IsFile(value.ToolName) || value.Working || value.ResourceRevision is null or < 0
                || run.Admission.Configuration!.Resources.Count(r => r.Key == value.ResourceKey && r.VirtualPath == args.Path) != 1) throw new AccessDeniedException("resource_source_changed");
        }
        if (args.Cursor is not null && value.ToolName == ToolCatalog.WorkspaceList) TenantWorkspaceRules.Path(owner, args.Cursor);
        if (value.SessionId != run.SessionId || session.Owner.SessionId != run.SessionId || session.Owner.TenantId != run.Owner.TenantId
            || agent.TenantId != run.Owner.TenantId || agent.Instance.InstanceId != run.AgentInstanceId || agent.PrincipalId != run.Owner.ExecutorPrincipalId
            || session.Owner.ExecutorPrincipalId != agent.PrincipalId || session.Owner.AgentInstanceId != agent.Instance.InstanceId
            || agent.Instance.Lifecycle != AgentInstanceLifecycle.Active || session.Snapshot.DurablyDeletedAt is not null || session.Snapshot.ArchivedAt is not null
            || session.Snapshot.LifecycleStatus != SessionLifecycleStatus.Active || session.Snapshot.Status is SessionStatus.Ending or SessionStatus.Ended
            || run.Admission.OutputContract != AgentRunOutputContract.ConversationResponse
            || run.Admission.ExecutionContext?.Origin is not (ExecutionOrigin.UserMessage or ExecutionOrigin.ApprovalContinuation)
            || !TenantWorkspaceReadContract.Allows(run.Admission.Configuration!.Definition, value.ToolName) || !TenantWorkspaceReadContract.Allows(current, value.ToolName))
            throw new AccessDeniedException("workspace_source_changed");
        if (value.ResourceKey is null && !value.Working && StateIsolationAccess.RestrictDefinition(run.Admission.Configuration.Definition, current).StateIsolationPolicy!.HomeAccess == HomeAccess.Disabled)
            throw new AccessDeniedException("home_access_disabled");
    }
    public static void RequireResourceRevision(PreparedWorkspaceRead value, AgentRun run, TenantAgent agent, InstanceResourceSnapshot resources)
    {
        if (value.ResourceKey is not { } key) return;
        if (key.StartsWith("instance:", StringComparison.Ordinal))
        {
            var current = resources.InstanceResources.SingleOrDefault(r => "instance:" + r.ResourceId.ToString("D") == key);
            var pinned = run.Admission.Configuration!.Resources.SingleOrDefault(r => r.Key == key);
            if (current is null || !current.Enabled || current.Revision != value.ResourceRevision || pinned is null
                || current.ContentSha256 != pinned.ContentSha256 || current.ByteLength != pinned.ByteLength || current.MediaType != pinned.MediaType
                || current.LogicalPath != pinned.LogicalPath || current.Kind != pinned.Kind) throw new SecurityConflictException("resource_source_changed");
        }
        else
        {
            var current = resources.DefinitionStates.SingleOrDefault(r => "definition:" + r.ResourceId.ToString("D") == key);
            if ((current?.Revision ?? 0) != value.ResourceRevision || current?.EnabledOverride == false
                || agent.Instance.DefinitionId != run.DefinitionId || agent.Instance.ActiveVersion != run.DefinitionVersion) throw new SecurityConflictException("resource_source_changed");
        }
    }
    public static async ValueTask PermissionsAsync(TenantSecurityState state, Guid instance, PreparedWorkspaceRead value, Guid principal, IamAuthorizer iam, CancellationToken ct)
    {
        foreach (var (key, scope) in Targets(state.Tenant.TenantId, instance, value))
            if (!(await iam.CheckOwnedTenantAsync(state, principal, PermissionCatalog.Get(key), scope, ct).ConfigureAwait(false)).Allowed) throw new AccessDeniedException("executor_read_required");
    }
    public static async ValueTask<bool> IndependentAsync(TenantSecurityState state, Guid instance, PreparedWorkspaceRead value, Guid? requester, IamAuthorizer iam, CancellationToken ct)
    {
        foreach (var (key, scope) in Targets(state.Tenant.TenantId, instance, value))
            if (TenantOperationPolicyService.Effective(state, PermissionCatalog.Get(key)).IndependentReview &&
                (key is "workspaces.read" or "resources.read" || await TenantSandboxReadOperationRules.NeedsReviewAsync(state, requester, key, scope, iam, ct).ConfigureAwait(false))) return true;
        return false;
    }
    public static async ValueTask ReviewerAsync(TenantSecurityState state, Guid instance, PreparedWorkspaceRead value, Guid? requester, Guid human, IamAuthorizer iam, CancellationToken ct)
    {
        if (!state.Users.Any(u => u.PrincipalId == human && u.Status == IdentityStatus.Active)) throw new AccessDeniedException("approver_unqualified");
        await PermissionsAsync(state, instance, value, human, iam, ct).ConfigureAwait(false);
        foreach (var (key, scope) in Targets(state.Tenant.TenantId, instance, value))
        {
            if (key is not ("workspaces.read" or "resources.read") && !await TenantSandboxReadOperationRules.NeedsReviewAsync(state, requester, key, scope, iam, ct).ConfigureAwait(false)) continue;
            if (TenantOperationPolicyService.Effective(state, PermissionCatalog.Get(key)).IndependentReview && requester == human) throw new AccessDeniedException("independent_reviewer_required");
            if (!(await iam.CheckOwnedTenantAsync(state, human, PermissionCatalog.Get("approvals.approve"), scope, ct).ConfigureAwait(false)).Allowed) throw new AccessDeniedException("approver_unqualified");
        }
    }
    public static void ValidateEffect(TenantApprovalRequest request, TenantApprovalDecision decision, TenantOperationGrant? grant, AgentRun run, TenantOperationCommit commit, AgentRun? source)
    {
        TenantLocalEffectRules.ValidateApproval(request, decision, grant, run, commit, source);
        if (request.Operation.WorkspaceRead is null || request.Kind != ApprovalKind.Review || grant is not null
            || commit.Outcome is not (TenantOperationOutcome.Completed or TenantOperationOutcome.Denied) || commit.Outcome == TenantOperationOutcome.Completed && commit.ReasonCode != Reason
            || commit.ReasonCode.Length is < 1 or > 128 || commit.ReasonCode.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')) throw new SecurityConflictException("operation_mismatch");
    }
}
