using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tools;

public static class TenantCompletionToolContract
{
    public static bool IsTool(string name) => name is ToolCatalog.BackgroundList or ToolCatalog.BackgroundInspect
        or ToolCatalog.BackgroundTake or ToolCatalog.BackgroundAcknowledge;
    public static bool IsOffered(AgentDefinition definition, AgentContext? context, string name) =>
        IsTool(name) && context is { ModelSupportsTools: true, AgentInstanceId: not null, OwnedSessionId: not null, ExecutionContext: { } execution }
        && execution.SessionId == context.OwnedSessionId && execution.Origin is
            (ExecutionOrigin.UserMessage or ExecutionOrigin.ImmediateBackground or ExecutionOrigin.ApprovalContinuation)
        && context.OutputContract is (AgentRunOutputContract.ConversationResponse or AgentRunOutputContract.BackgroundOutcome)
        && (name != ToolCatalog.BackgroundAcknowledge || context.HasBackgroundClaim)
        && RolePermissions.AllowsTool(definition, name);
}
