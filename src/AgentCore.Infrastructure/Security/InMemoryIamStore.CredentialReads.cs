using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    private void RequireCredentialReadOperation(TenantApprovalRequest request, AgentRun run, TenantAgent agent)
    {
        var value = request.Operation.CredentialRead!;
        var session = sessionBacking.ReadTenantSession(request.TenantId, value.SessionId) ?? throw new AccessDeniedException("resource_unavailable");
        var snapshot = ReadTenantCredentialBindingsAsync(request.TenantId, agent.Instance.InstanceId).Result ?? throw new AccessDeniedException("resource_unavailable");
        TenantCredentialReadOperationRules.Require(value, run, agent, session, TenantAgentDefinitionBaseline(agent), snapshot.Bindings);
        TenantCredentialReadOperationRules.PermissionsAsync(_tenants[request.TenantId], value, agent.PrincipalId, new IamAuthorizer(this), CancellationToken.None).GetAwaiter().GetResult();
    }
}
