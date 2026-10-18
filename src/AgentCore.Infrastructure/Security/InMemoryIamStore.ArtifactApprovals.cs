using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    private TenantArtifactArguments RequireArtifactOperation(TenantApprovalRequest request, AgentRun run, TenantAgent agent, AgentRun? source = null)
    {
        var session = sessionBacking.ReadTenantSession(request.TenantId, request.SessionId) ?? throw new AccessDeniedException("resource_unavailable");
        var args = TenantArtifactReviewRules.Require(request, run, agent, TenantAgentDefinitionBaseline(agent), session, source);
        TenantArtifactReviewRules.ExecutorAsync(_tenants[request.TenantId], request, new IamAuthorizer(this), CancellationToken.None).GetAwaiter().GetResult();
        return args;
    }
}
