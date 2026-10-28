using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Domain.Workflows;

namespace AgentCore.Application.Workflows;

public static class WorkflowCancellationRules
{
    public static async ValueTask<WorkflowExecution> RequireAsync(WorkflowCancelCommand command, TenantSecurityState state,
        WorkflowExecution before, IamAuthorizer iam, DateTimeOffset now, CancellationToken ct)
    {
        _ = WorkflowCatalogRules.RequestKey(command.RequestKey);
        if (!state.Users.Any(u => u.PrincipalId == command.Human.PrincipalId && u.AccountId == command.Human.AccountId && u.Status == IdentityStatus.Active)
            || !(await iam.CheckOwnedTenantAsync(state, command.Human.PrincipalId, PermissionCatalog.Get("workflows.cancel"), WorkflowCatalogAuthority.Scope(command.TenantId, before.WorkflowId), ct)).Allowed)
            throw new AccessDeniedException("resource_unavailable");
        var fingerprint = WorkflowExecutionRules.Hash($"{command.ExecutionId:D}:{command.Human.PrincipalId:D}:cancel");
        if (before.Cancellation is { } previous)
        {
            if (previous.PrincipalId != command.Human.PrincipalId || previous.RequestKey != command.RequestKey || previous.Fingerprint != fingerprint)
                throw new SecurityConflictException("workflow_cancel_already_requested");
            return before;
        }
        if (WorkflowExecutionRules.Terminal(before.Status) || before.Revision != command.ExpectedRevision || state.Tenant.Revision != command.ExpectedSecurityRevision)
            throw new SecurityConflictException("stale_revision");
        return before with { Revision = before.Revision + 1, CancellationRequested = true,
            Cancellation = new(command.Human.PrincipalId, command.RequestKey, fingerprint, now) };
    }
}
