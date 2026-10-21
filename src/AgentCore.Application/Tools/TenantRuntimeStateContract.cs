using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tools;

public static class TenantRuntimeStateContract
{
    public static bool Allows(AgentDefinition definition) => RolePermissions.AllowsTool(definition, ToolCatalog.AgentStateInspect)
        && (definition.StateIsolationPolicy?.AgentStateVisibility ?? AgentStateVisibility.Off) != AgentStateVisibility.Off;
    public static bool IsOffered(AgentDefinition definition, AgentContext context) => Allows(definition)
        && context is { ModelSupportsTools: true, AgentInstanceId: not null, OwnedSessionId: not null, ExecutionContext: { } execution }
        && execution.SessionId == context.OwnedSessionId && TenantTaskExecutionContract.Allows(context);
}
