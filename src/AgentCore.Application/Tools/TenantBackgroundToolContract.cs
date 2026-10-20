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
            ? context.OutputContract == AgentRunOutputContract.ConversationResponse && execution.Origin is (ExecutionOrigin.UserMessage or ExecutionOrigin.ApprovalContinuation) && RolePermissions.AllowsTool(definition, name)
            : name == ToolCatalog.WorkComplete && execution.Origin is (ExecutionOrigin.ImmediateBackground or ExecutionOrigin.ApprovalContinuation) && context.OutputContract == AgentRunOutputContract.BackgroundOutcome);
    public static bool Allows(AgentRun run, string name) => name == ToolCatalog.BackgroundStart
        ? AgentCore.Application.Security.TenantBackgroundCreationRules.Allows(run)
        : name == ToolCatalog.WorkComplete && run.Admission.ExecutionContext?.Origin is (ExecutionOrigin.ImmediateBackground or ExecutionOrigin.ApprovalContinuation) && run.Admission.OutputContract == AgentRunOutputContract.BackgroundOutcome;
}
