using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Ports;

public interface IConversationTurnRunner
{
    ValueTask<bool> DispatchAsync(ConversationTurnExecution execution, CancellationToken cancellationToken = default);
}
