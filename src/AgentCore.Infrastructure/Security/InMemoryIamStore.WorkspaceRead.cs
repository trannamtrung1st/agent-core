using AgentCore.Application.Security;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    private void RequireWorkspaceReadOperation(TenantApprovalRequest request, AgentRun run, TenantAgent agent)
    {
        var value = request.Operation.WorkspaceRead!;
        var session = sessionBacking.ReadTenantSession(request.TenantId, value.SessionId) ?? throw new AccessDeniedException("resource_unavailable");
        var current = AgentCore.Application.Agents.InstanceSettingsResolver.Resolve(TenantAgentDefinitionBaseline(agent), agent.Instance.SettingsOverrides);
        TenantWorkspaceReadOperationRules.Require(value, run, agent, current, session);
        if (value.ResourceKey is not null) TenantWorkspaceReadOperationRules.RequireResourceRevision(value, run, agent, instances.ReadTenantResourcesAsync(agent.Instance.InstanceId).Result);
        TenantWorkspaceReadOperationRules.PermissionsAsync(_tenants[request.TenantId], agent.Instance.InstanceId, value, agent.PrincipalId, new IamAuthorizer(this), CancellationToken.None).GetAwaiter().GetResult();
    }
    private bool WorkspaceApprovalVisible(TenantApprovalRequest request, TenantWorkspaceReadApprovalVisibility? visible)
    {
        var value = request.Operation.WorkspaceRead!;
        var session = sessionBacking.ReadTenantSession(request.TenantId, value.SessionId);
        var agent = FindTenantAgentAsync(request.TenantId, request.Operation.InstanceId).Result;
        return session is not null && session.Snapshot.DurablyDeletedAt is null && agent is not null && agent.PrincipalId == request.ExecutorPrincipalId
            && session.Owner.AgentInstanceId == agent.Instance.InstanceId && session.Owner.ExecutorPrincipalId == agent.PrincipalId
            && (value.ResourceKey is null || ChildCatalog(request.TenantId, agent.Instance.InstanceId, null, CancellationToken.None)
                .Contains(TenantAgentChildScopes.Scope(request.TenantId, agent.Instance.InstanceId, "resource", value.ResourceKey)))
            && (value.ResourceKey is null ? visible?.Workspaces is null || visible.Workspaces.Contains(request.ResourceId)
                : visible?.Resources is null || visible.Resources.Contains(request.ResourceId))
            && (value.Working || visible?.HomeAgents is null || visible.HomeAgents.Contains(agent.Instance.InstanceId))
            && (visible?.Sessions is null || visible.Sessions.Contains(value.SessionId));
    }

}
