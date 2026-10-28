using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Domain.Workflows;

namespace AgentCore.Application.Workflows;

public static class WorkflowParticipantPolicy
{
    public static async ValueTask RequireAsync(TenantSecurityState state, WorkflowExecution execution, IamAuthorizer iam, CancellationToken ct)
    {
        foreach (var key in new[] { "sessions.create", "agent-runs.create" })
            if (await TenantSandboxReadOperationRules.NeedsReviewAsync(state, execution.Source.OriginalHumanPrincipalId, key, AccessScope.ForTenant(execution.TenantId), iam, ct))
                throw new AccessDeniedException("workflow_task_review_required");
        var mode = TenantOperationPolicyService.Effective(state, PermissionCatalog.Get("tasks.delegate")).Mode;
        if (mode == OperationApprovalMode.Always || mode == OperationApprovalMode.WhenRequesterLacksPermission
            && execution.Source.OriginalHumanPrincipalId is { } human
            && !(await iam.CheckOwnedTenantAsync(state, human, PermissionCatalog.Get("tasks.delegate"), WorkflowCatalogAuthority.Scope(execution.TenantId, execution.WorkflowId), ct)).Allowed)
            throw new AccessDeniedException("workflow_task_review_required");
    }
}
