using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Workflows;

public static class WorkflowCatalogAuthority
{
    public static bool TryScope(AccessScope scope, out Guid id)
    {
        id = Guid.Empty;
        return scope.Kind == AccessScopeKind.Resource && scope.TenantId is { } tenant && tenant != Guid.Empty && scope.ResourceType == "workflow"
            && Guid.TryParseExact(scope.ResourceId, "D", out id) && id != Guid.Empty && scope.ResourceId == id.ToString("D");
    }
    public static AccessScope Scope(Guid tenant, Guid workflow) => AccessScope.ForResource(tenant, "workflow", workflow.ToString("D"));
    public static void Command(WorkflowCatalogWrite command)
    {
        var value = command.Definition; var audit = command.Audit;
        if (value.TenantId != command.NextSecurity.Tenant.TenantId || audit.TenantId != value.TenantId
            || audit.ActorPrincipalId != command.Human.PrincipalId || audit.Target != value.WorkflowId.ToString("D")
            || command.NextSecurity.Tenant.Revision != command.ExpectedSecurityRevision + 1 || audit.Decision != "allowed"
            || command.Fingerprint.Length != 64) throw new ArgumentException("Invalid workflow catalog transaction.");
        _ = WorkflowCatalogRulesKey(command.RequestKey);
    }
    private static string WorkflowCatalogRulesKey(string key) => AgentCore.Domain.Workflows.WorkflowCatalogRules.RequestKey(key);
    public static async ValueTask RequireAsync(TenantSecurityState state, WorkflowCatalogWrite command, bool creating,
        IamAuthorizer authorizer, CancellationToken ct)
    {
        Command(command);
        var scope = creating ? AccessScope.ForTenant(state.Tenant.TenantId) : Scope(state.Tenant.TenantId, command.Definition.WorkflowId);
        var permission = PermissionCatalog.Get(command.Audit.Action);
        if (!(await authorizer.CheckOwnedTenantAsync(state, command.Human.PrincipalId, permission, scope, ct)).Allowed
            || !creating && !(await authorizer.CheckOwnedTenantAsync(state, command.Human.PrincipalId, PermissionCatalog.Get("workflows.read"), scope, ct)).Allowed)
            throw new AccessDeniedException("permission_denied");
    }
}
