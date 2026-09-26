using AgentCore.Api.Realtime;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;

namespace AgentCore.Api;

public sealed class ConversationTurnRunner(SessionHost host) : IConversationTurnRunner
{
    public ValueTask<bool> DispatchAsync(ConversationTurnExecution execution, CancellationToken cancellationToken = default) =>
        host.DispatchConversationTurnAsync(execution, cancellationToken);
}
