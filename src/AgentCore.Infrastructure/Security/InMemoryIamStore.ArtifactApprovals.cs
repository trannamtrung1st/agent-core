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
        if (request.Operation.ArtifactPublication?.Source is { ResourceKey: { } key } saved) TenantResourceRevisionRules.Require(key, saved.ResourceRevision, run, agent,
            instances.ReadTenantResourcesAsync(agent.Instance.InstanceId).Result);
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
            && (source.ResourceKey is { } key
                ? ChildCatalog(request.TenantId, agent.Instance.InstanceId, null, CancellationToken.None).Contains(TenantAgentChildScopes.Scope(request.TenantId, agent.Instance.InstanceId, "resource", key))
                    && (visible?.Resources is null || visible.Resources.Contains(agent.Instance.InstanceId.ToString("D") + ":" + key))
                : visible?.Workspaces is null || visible.Workspaces.Contains(scope.ResourceId!))
            && (source.Working || visible?.HomeAgents is null || visible.HomeAgents.Contains(agent.Instance.InstanceId))
            && (visible?.Sessions is null || visible.Sessions.Contains(request.SessionId));
    }
}
