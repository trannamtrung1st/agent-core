using AgentCore.Api.Realtime;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;

namespace AgentCore.Api;

public sealed class AgentRunDispatcher(SessionHost host) : IAgentRunDispatcher
{
    public ValueTask<bool> AdmitCompletionAsync(BackgroundCompletionCandidate source, CancellationToken cancellationToken = default) =>
        host.AdmitBackgroundCompletionAsync(source, cancellationToken);

    public ValueTask<bool> RepairPendingInputsAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        host.RepairPendingAgentInputsAsync(sessionId, cancellationToken);

    public ValueTask<bool> DispatchAsync(AgentRun run, CancellationToken cancellationToken = default) =>
        host.DispatchAgentRunAsync(run, cancellationToken);
}
