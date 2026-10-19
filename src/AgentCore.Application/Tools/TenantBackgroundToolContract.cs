using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tools;

public static class TenantBackgroundToolContract
{
    public static bool IsTool(string name) => name is ToolCatalog.BackgroundStart or ToolCatalog.WorkComplete;
    public static bool IsOffered(AgentDefinition definition, AgentContext? context, string name) =>
        context is { ModelSupportsTools: true, AgentInstanceId: not null, OwnedSessionId: not null, ExecutionContext: { } execution }
        && execution.SessionId == context.OwnedSessionId
        && (name == ToolCatalog.BackgroundStart
            ? execution.Origin == ExecutionOrigin.UserMessage && !context.DetachedExecution && context.Trigger.Kind == TriggerKind.UserTurn && RolePermissions.AllowsTool(definition, name)
            : name == ToolCatalog.WorkComplete && execution.Origin == ExecutionOrigin.ImmediateBackground && context.OutputContract == AgentRunOutputContract.BackgroundOutcome);
    public static bool Allows(AgentRun run, string name) => name == ToolCatalog.BackgroundStart
        ? run.Admission.ExecutionContext?.Origin == ExecutionOrigin.UserMessage && run.Admission.Activation.Kind == ActivationKind.UserTurn
        : name == ToolCatalog.WorkComplete && run.Admission.ExecutionContext?.Origin == ExecutionOrigin.ImmediateBackground && run.Admission.OutputContract == AgentRunOutputContract.BackgroundOutcome;
}
