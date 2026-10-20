using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantBackgroundCreationRules
{
    public const string AuditAction = "operations.background.admit";
    public static readonly string[] Permissions = ["sessions.create", "agent-runs.create"];
    public static bool Allows(AgentRun run) => TenantTaskExecutionContract.Allows(run);
    public static string Hash(ResolvedAgentRunConfiguration value) => TenantWorkspaceMutationContract.Hash(JsonSerializer.Serialize(value));
    public static void Require(TenantApprovalRequest request, AgentRun run, TenantAgent agent, AgentCore.Domain.Definitions.AgentDefinition current)
    {
        var value = request.Operation.BackgroundStart ?? throw new SecurityConflictException("operation_mismatch");
        if (!TenantTaskExecutionContract.Allows(run, current) || value.TenantId != request.TenantId || value.SessionId != run.SessionId
            || agent.PrincipalId != run.Owner.ExecutorPrincipalId || agent.Instance.InstanceId != request.Operation.InstanceId
            || !RolePermissions.AllowsTool(run.Admission.Configuration!.Definition, ToolCatalog.BackgroundStart)
            || !RolePermissions.AllowsTool(current, ToolCatalog.BackgroundStart)) throw new AccessDeniedException("background_unavailable");
    }
    public static async ValueTask ExecutorAsync(TenantSecurityState state, TenantApprovalRequest request, IamAuthorizer iam, CancellationToken ct)
    {
        foreach (var key in Permissions)
            if ((request.Kind == ApprovalKind.Review || key != request.Permission)
                && !(await iam.CheckOwnedTenantAsync(state, request.ExecutorPrincipalId, PermissionCatalog.Get(key), AccessScope.ForTenant(request.TenantId), ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("operation_authority_changed");
    }
    public static async ValueTask<bool> CompoundReviewAsync(TenantSecurityState state, Guid? requester, string primary, IamAuthorizer iam, CancellationToken ct)
    {
        foreach (var key in Permissions.Where(k => k != primary))
            if (await TenantSandboxReadOperationRules.NeedsReviewAsync(state, requester, key, AccessScope.ForTenant(state.Tenant.TenantId), iam, ct).ConfigureAwait(false)) return true;
        return false;
    }
    public static async ValueTask ReadAsync(TenantSecurityState state, TenantApprovalRequest request, Guid human, IamAuthorizer iam, CancellationToken ct)
    {
        foreach (var key in Permissions)
            if (!(await iam.CheckOwnedTenantAsync(state, human, PermissionCatalog.Get(key), AccessScope.ForTenant(request.TenantId), ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("approval_unavailable");
        if (!(await iam.CheckOwnedTenantAsync(state, human, PermissionCatalog.Get("sessions.read"), TenantSessionRules.Scope(request.TenantId, request.SessionId), ct).ConfigureAwait(false)).Allowed)
            throw new AccessDeniedException("approval_unavailable");
    }
    public static async ValueTask<bool> IndependentAsync(TenantSecurityState state, TenantApprovalRequest request, IamAuthorizer iam, CancellationToken ct)
    {
        foreach (var key in Permissions)
            if (TenantOperationPolicyService.Effective(state, PermissionCatalog.Get(key)).IndependentReview
                && (key == request.Permission || await TenantSandboxReadOperationRules.NeedsReviewAsync(state, request.OriginalInitiatorPrincipalId, key, AccessScope.ForTenant(request.TenantId), iam, ct).ConfigureAwait(false))) return true;
        return false;
    }
    public static async ValueTask ReviewerAsync(TenantSecurityState state, TenantApprovalRequest request, Guid human, IamAuthorizer iam, CancellationToken ct)
    {
        await ReadAsync(state, request, human, iam, ct).ConfigureAwait(false);
        foreach (var key in Permissions)
        {
            var scope = AccessScope.ForTenant(request.TenantId);
            if (key != request.Permission && !await TenantSandboxReadOperationRules.NeedsReviewAsync(state, request.OriginalInitiatorPrincipalId, key, scope, iam, ct).ConfigureAwait(false)) continue;
            if (TenantOperationPolicyService.Effective(state, PermissionCatalog.Get(key)).IndependentReview && human == request.OriginalInitiatorPrincipalId)
                throw new AccessDeniedException("independent_reviewer_required");
            if (!(await iam.CheckOwnedTenantAsync(state, human, PermissionCatalog.Get("approvals.approve"), scope, ct).ConfigureAwait(false)).Allowed
                || key == request.Permission && request.Kind == ApprovalKind.ExceptionalGrant
                    && !(await iam.CheckOwnedTenantAsync(state, human, PermissionCatalog.Get("approvals.grant_exception"), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("approver_unqualified");
        }
    }
    public static void ValidateEffect(TenantApprovalRequest request, TenantApprovalDecision decision, TenantOperationGrant? grant, AgentRun run, TenantOperationCommit commit, AgentRun? source)
    {
        TenantLocalEffectRules.ValidateApproval(request, decision, grant, run, commit, source);
        if (commit.Outcome is not (TenantOperationOutcome.Completed or TenantOperationOutcome.Denied)
            || commit.Outcome == TenantOperationOutcome.Completed && (commit.ReasonCode != "background_created" || commit.BackgroundAdmission is null))
            throw new SecurityConflictException("operation_mismatch");
    }
    public static TenantSessionRoot Admission(TenantApprovalRequest request, AgentRun parent, TenantSession session, TenantAgent agent,
        TenantBackgroundAdmission admission, DateTimeOffset at)
    {
        var value = request.Operation.BackgroundStart!;
        var child = admission.Run; var snapshot = admission.Session;
        var resolved = new ResolvedAgentRunConfiguration(child.Admission.Configuration!, child.PinnedPersona, child.PinnedSkillCatalog, agent.Instance.ExecutionBudgets);
        if (child.SessionId != value.ChildSessionId || child.AgentRunId != value.RunId || child.ActivationId != value.ActivationId
            || child.ResponseId != value.ResponseId || snapshot.Entries.Count != 1 || snapshot.Entries[0].EntryId != value.EntryId
            || snapshot.Entries[0].Text != value.Objective || snapshot.Title != value.Title || snapshot.Origin.ReportCompletionToOrigin != value.ReportCompletion
            || JsonSerializer.Serialize(child.PinnedModel) != JsonSerializer.Serialize(value.Model) || Hash(resolved) != value.ConfigurationHash
            || child.Admission.Activation.DedupeKey != "background:operation:" + request.Operation.OperationId.ToString("D"))
            throw new SecurityConflictException("operation_mismatch");
        return TenantBackgroundAdmissionRules.Shape(new(parent.Owner, parent.SessionId, parent.AgentRunId, parent.Claim!.Generation), session, parent, snapshot, child, agent, at);
    }
}
