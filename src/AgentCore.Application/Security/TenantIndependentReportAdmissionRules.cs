using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

// Independent occurrences retain their native admission and exact configured destination.
public static class TenantIndependentReportAdmissionRules
{
    public static async ValueTask AuthorizeReportAsync(TenantSecurityState state, AgentRun sourceRoot, AgentDefinition current,
        IamAuthorizer authorizer, ITenantRunStore runs, CancellationToken ct, TenantBackgroundCompletion source)
    {
        var execution = sourceRoot.Admission.ExecutionContext ?? throw new AccessDeniedException("tenant_context_required");
        if (state.Tenant.Status != IdentityStatus.Active || state.Tenant.TenantId != execution.TenantId
            || !TenantCompletionDestinationRules.Configured(source) || !TenantTaskExecutionContract.Allows(sourceRoot, current))
            throw new AccessDeniedException("background_unavailable");
        var permission = PermissionCatalog.Get("agent-runs.create"); var scope = AccessScope.ForTenant(execution.TenantId);
        if (!(await authorizer.CheckTenantAsync(state, execution.AgentPrincipalId, permission, scope, ct).ConfigureAwait(false)).Allowed)
            throw new AccessDeniedException("permission_denied");
        var mode = TenantOperationPolicyService.Effective(state, permission).Mode;
        if (mode == OperationApprovalMode.Always || mode == OperationApprovalMode.WhenRequesterLacksPermission
            && execution.OriginalInitiatorPrincipalId is { } requester
            && !(await authorizer.CheckTenantAsync(state, requester, permission, scope, ct).ConfigureAwait(false)).Allowed)
            throw new AccessDeniedException("background_review_required");
        await TenantExecutionLineage.RequireRequesterAsync(sourceRoot, state, authorizer, runs, ct).ConfigureAwait(false);
    }
}
