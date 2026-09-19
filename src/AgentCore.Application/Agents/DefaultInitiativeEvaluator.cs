using AgentCore.Application.Ports;

namespace AgentCore.Application.Agents;

public sealed class DefaultInitiativeEvaluator(PromptContextBuilder builder, ILanguageModel initiativeModel)
    : IInitiativeEvaluator
{
    public ValueTask<AgentDecision> EvaluateAsync(
        AgentContext context,
        Guid responseId,
        CancellationToken cancellationToken = default) =>
        InitiativeEvaluator.EvaluateAsync(
            context.LanguageModel ?? initiativeModel,
            builder,
            context,
            responseId,
            cancellationToken);
}
