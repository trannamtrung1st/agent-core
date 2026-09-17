using AgentCore.Application.Agents;

namespace AgentCore.Application.Ports;

public interface IInitiativeEvaluator
{
    ValueTask<AgentDecision> EvaluateAsync(
        AgentContext context,
        Guid responseId,
        CancellationToken cancellationToken = default);
}
