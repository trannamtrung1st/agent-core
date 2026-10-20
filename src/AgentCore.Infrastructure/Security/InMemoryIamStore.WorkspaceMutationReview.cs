using AgentCore.Application.Security;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    private void RequireWorkspaceMutationOperation(TenantApprovalRequest request, AgentRun run, TenantAgent agent, AgentRun? source = null)
    {
        RequireTenantExecutionSourcesAsync(run, _tenants[request.TenantId], new IamAuthorizer(this), this, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        var session = sessionBacking.ReadTenantSession(request.TenantId, request.SessionId) ?? throw new AccessDeniedException("resource_unavailable");
        var current = AgentCore.Application.Agents.InstanceSettingsResolver.Resolve(TenantAgentDefinitionBaseline(agent), agent.Instance.SettingsOverrides);
        TenantWorkspaceMutationOperationRules.Require(request, run, agent, current, session, source);
        TenantWorkspaceMutationOperationRules.ExecutorAsync(_tenants[request.TenantId], request, new IamAuthorizer(this), CancellationToken.None).GetAwaiter().GetResult();
    }
    private bool WorkspaceMutationApprovalVisible(TenantApprovalRequest request, TenantWorkspaceMutationApprovalVisibility? visible)
    {
        var session = sessionBacking.ReadTenantSession(request.TenantId, request.SessionId);
        var agent = FindTenantAgentAsync(request.TenantId, request.Operation.InstanceId).Result;
        if (session is null || session.Snapshot.DurablyDeletedAt is not null || agent is null || agent.PrincipalId != request.ExecutorPrincipalId
            || session.Owner.AgentInstanceId != agent.Instance.InstanceId || session.Owner.ExecutorPrincipalId != agent.PrincipalId
            || visible?.Sessions is not null && !visible.Sessions.Contains(request.SessionId)) return false;
        foreach (var target in TenantApprovalRules.MutationScopes(TenantWorkspaceMutationOperationRules.Arguments(request.Operation.WorkspaceMutation!)))
        {
            if (!target.Working && visible?.HomeAgents is not null && !visible.HomeAgents.Contains(agent.Instance.InstanceId)) return false;
            var resources = target.Permission == "workspaces.read" ? visible?.Reads : target.Permission == "workspaces.write" ? visible?.Writes : visible?.Deletes;
            var resource = target.Working ? "working:" + request.SessionId.ToString("D") : "home:" + agent.Instance.InstanceId.ToString("D");
            if (resources is not null && !resources.Contains(resource)) return false;
        }
        return true;
    }
}
