using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    private TenantArtifactPublicationArguments RequireArtifactOperation(TenantApprovalRequest request, AgentRun run, TenantAgent agent, AgentRun? source = null)
    {
        var session = sessionBacking.ReadTenantSession(request.TenantId, request.SessionId) ?? throw new AccessDeniedException("resource_unavailable");
        var args = TenantArtifactReviewRules.Require(request, run, agent, TenantAgentDefinitionBaseline(agent), session, source);
        TenantArtifactReviewRules.ExecutorAsync(_tenants[request.TenantId], request, new IamAuthorizer(this), CancellationToken.None).GetAwaiter().GetResult();
        return args;
    }
    private bool ArtifactSourceApprovalVisible(TenantApprovalRequest request, TenantWorkspaceReadApprovalVisibility? visible)
    {
        var source = request.Operation.ArtifactPublication!.Source!;
        var session = sessionBacking.ReadTenantSession(request.TenantId, request.SessionId);
        var agent = FindTenantAgentAsync(request.TenantId, request.Operation.InstanceId).Result;
        var scope = TenantWorkspaceRules.Scope(new(request.TenantId, request.Operation.InstanceId, source.Working ? request.SessionId : null));
        return session is not null && session.Snapshot.DurablyDeletedAt is null && agent is not null && agent.PrincipalId == request.ExecutorPrincipalId
            && session.Owner.AgentInstanceId == agent.Instance.InstanceId && session.Owner.ExecutorPrincipalId == agent.PrincipalId
            && (visible?.Workspaces is null || visible.Workspaces.Contains(scope.ResourceId!))
            && (source.Working || visible?.HomeAgents is null || visible.HomeAgents.Contains(agent.Instance.InstanceId))
            && (visible?.Sessions is null || visible.Sessions.Contains(request.SessionId));
    }
}
