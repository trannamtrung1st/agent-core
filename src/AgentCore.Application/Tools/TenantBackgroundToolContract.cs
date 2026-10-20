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
            ? TenantTaskExecutionContract.Allows(context) && RolePermissions.AllowsTool(definition, name)
            : name == ToolCatalog.WorkComplete && IsWorkOrigin(execution.Origin) && context.OutputContract == AgentRunOutputContract.BackgroundOutcome);
    public static bool Allows(AgentRun run, string name) => name == ToolCatalog.BackgroundStart
        ? AgentCore.Application.Security.TenantBackgroundCreationRules.Allows(run)
        : name == ToolCatalog.WorkComplete && run.Admission.ExecutionContext is { } execution && IsWorkOrigin(execution.Origin) && run.Admission.OutputContract == AgentRunOutputContract.BackgroundOutcome;
    private static bool IsWorkOrigin(ExecutionOrigin origin) => origin is ExecutionOrigin.ImmediateBackground or ExecutionOrigin.ApprovalContinuation
        or ExecutionOrigin.ScheduledOccurrence or ExecutionOrigin.ManualInvocation or ExecutionOrigin.ApplicationEvent or ExecutionOrigin.CoreEvent;
}
