using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    private void RequireAutomationContextAuthoring(TenantSecurityState state, TenantAgent agent, Automation? current, Automation proposed, Guid? human, CancellationToken ct, bool force = false)
    {
        if (!force && !TenantAutomationContextRules.CheckAuthoring(current, proposed)) return;
        if (proposed.ExecutionTarget.Workflow is { } workflow)
        {
            var version = workflowVersions.GetValueOrDefault((state.Tenant.TenantId, workflow.WorkflowId, workflow.Version)) ?? throw new AccessDeniedException("workflow_version_unavailable");
            if (workflowDefinitions.GetValueOrDefault((state.Tenant.TenantId, workflow.WorkflowId)) is not { Retired: false }) throw new AccessDeniedException("workflow_version_unavailable");
            AgentCore.Application.Workflows.WorkflowAutomationRules.DestinationAsync(state, proposed, version, agent.PrincipalId, new(this), ct).GetAwaiter().GetResult();
            AgentCore.Application.Workflows.WorkflowAutomationRules.AuthorityAsync(state, proposed, agent, InstanceSettingsResolver.Resolve(TenantAgentDefinitionBaseline(agent), agent.Instance.SettingsOverrides), human, null, new(this), ct).GetAwaiter().GetResult();
            return;
        }
        var tenant = state.Tenant.TenantId;
        var own = FindPrivateSpaceAsync(tenant, agent.Instance.InstanceId, ct).Result ?? throw new AccessDeniedException("private_space_unavailable");
        var id = proposed.PinnedSpaceId ?? own.SpaceId;
        var active = FindSpaceAsync(tenant, id, ct).Result;
        var membership = _spaceMemberships.GetValueOrDefault((tenant, id, agent.Instance.InstanceId));
        var environment = proposed.PinnedEnvironmentId is { } profile ? FindEnvironmentAsync(tenant, id, profile, ct).Result : null;
        var target = proposed.ExecutionTarget.SessionId is { } session ? sessionBacking.ReadTenantSession(tenant, session) : null;
        TenantAutomationContextRules.Require(proposed, agent, own, active, membership, environment, target);
        TenantAutomationContextRules.PermissionsAsync(state, agent.PrincipalId, id, environment is not null, human, new IamAuthorizer(this), ct).GetAwaiter().GetResult();
    }
    private void RequireAutomationContextUnavailable(TenantSecurityState state, TenantAgent agent, Automation current, CancellationToken ct)
    {
        try { RequireAutomationContextAuthoring(state, agent, current, current, null, ct, force: true); }
        catch (AccessDeniedException error) when (TenantAutomationContextRules.Failure(error.ReasonCode) is not null) { return; }
        throw new SecurityConflictException("automation_context_available");
    }
}
