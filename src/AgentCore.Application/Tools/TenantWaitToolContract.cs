using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tools;

public static class TenantWaitToolContract
{
    public static bool IsOffered(AgentDefinition definition, AgentContext? context) =>
        context is { ModelSupportsTools: true, AgentInstanceId: not null, OwnedSessionId: not null, ExecutionContext: { } execution }
        && execution.SessionId == context.OwnedSessionId && execution.Origin is
            (ExecutionOrigin.UserMessage or ExecutionOrigin.ImmediateBackground or ExecutionOrigin.ApprovalContinuation)
        && context.OutputContract is (AgentRunOutputContract.ConversationResponse or AgentRunOutputContract.BackgroundOutcome)
        && RolePermissions.AllowsTool(definition, ToolCatalog.ExecutionWait);
}
