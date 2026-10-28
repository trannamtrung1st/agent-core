using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Domain.Workflows;

namespace AgentCore.Application.Workflows;

public static class WorkflowReadRules
{
    public static async ValueTask<bool> CanReadAsync(TenantSecurityState state, Guid actor, WorkflowExecution execution,
        IamAuthorizer iam, ITenantSessionStore sessions, ITenantRunStore runs, CancellationToken ct)
    {
        async ValueTask<bool> Allowed(string permission, AccessScope scope) => (await iam.CheckOwnedTenantAsync(state, actor, PermissionCatalog.Get(permission), scope, ct)).Allowed;
        var scope = WorkflowCatalogAuthority.Scope(execution.TenantId, execution.WorkflowId);
        if (execution.TenantId != state.Tenant.TenantId || !await Allowed("workflows.read", scope) || !await Allowed("workflows.monitor", scope)) return false;
        if (execution.Source.SessionId is { } session && (await sessions.FindTenantSessionAsync(execution.TenantId, session, ct) is null
            || !await Allowed("sessions.read", TenantSessionRules.Scope(execution.TenantId, session)))) return false;
        if (execution.Source.RunId is { } run && (await runs.FindTenantRunAsync(execution.TenantId, run, ct) is null
            || !await Allowed("agent-runs.read", TenantRunRules.Scope(execution.TenantId, run)))) return false;
        if (execution.Source.AutomationId is { } automation && !await Allowed("automations.read", AccessScope.ForResource(execution.TenantId, "automation", automation.ToString("D")))) return false;
        return true;
    }
}
