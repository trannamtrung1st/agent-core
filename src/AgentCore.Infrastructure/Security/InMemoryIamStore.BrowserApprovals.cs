using AgentCore.Application.Security;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    private void RequireBrowserOperation(TenantApprovalRequest request, AgentRun run, TenantAgent agent)
    {
        var value = request.Operation.BrowserCommand!;
        var session = sessionBacking.ReadTenantSession(request.TenantId, value.SessionId) ?? throw new AccessDeniedException("resource_unavailable");
        TenantBrowserOperationRules.Require(value, run, agent, SandboxBaseline(agent), session);
        if (request.Snapshot != TenantBrowserOperationRules.Snapshot(agent, value)) throw new SecurityConflictException("browser_target_changed");
        TenantBrowserOperationRules.RequirePermissionsAsync(_tenants[request.TenantId], agent.Instance.InstanceId, value, request.ExecutorPrincipalId,
            new IamAuthorizer(this), CancellationToken.None).GetAwaiter().GetResult();
    }
}
