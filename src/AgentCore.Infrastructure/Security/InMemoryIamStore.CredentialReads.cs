using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    private static bool CredentialFamilyApprovalVisible(PreparedCredentialRead read, TenantCredentialReadApprovalVisibility? visibility)
    {
        var family = AgentCore.Application.Tools.TenantCredentialDiscoveryContract.Parse(read.ArgumentsJson).Family;
        var allowed = family == "email" ? visibility?.EmailCredentials : family == "drive" ? visibility?.DriveCredentials : null;
        return allowed is null || TenantApprovalRules.CredentialReadReferences(read).All(r => allowed.Contains(r.CredentialId));
    }
    private void RequireCredentialReadOperation(TenantApprovalRequest request, AgentRun run, TenantAgent agent)
    {
        var value = request.Operation.CredentialRead!;
        var session = sessionBacking.ReadTenantSession(request.TenantId, value.SessionId) ?? throw new AccessDeniedException("resource_unavailable");
        var snapshot = ContextCredentialBindings(run);
        TenantCredentialReadOperationRules.Require(value, run, agent, session, TenantAgentDefinitionBaseline(agent), snapshot);
        TenantCredentialReadOperationRules.PermissionsAsync(_tenants[request.TenantId], value, agent.PrincipalId, new IamAuthorizer(this), CancellationToken.None).GetAwaiter().GetResult();
    }
}
