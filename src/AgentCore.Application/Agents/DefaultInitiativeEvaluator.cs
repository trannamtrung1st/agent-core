using AgentCore.Application.Ports;

namespace AgentCore.Application.Agents;

public sealed class DefaultInitiativeEvaluator(PromptContextBuilder builder)
    : IInitiativeEvaluator
{
    public ValueTask<AgentDecision> EvaluateAsync(
        AgentContext context,
        Guid responseId,
        CancellationToken cancellationToken = default) => context.LanguageModel is not { } model
        ? ValueTask.FromResult<AgentDecision>(new StaySilent("Initiative model authority is unavailable.", CountsTowardSilentCap: false))
        : InitiativeEvaluator.EvaluateAsync(
            model,
            builder,
            context,
            responseId,
            cancellationToken);
}
