using AgentCore.Application.Admin;
using AgentCore.Application.Agents;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Infrastructure.Admin;

public sealed class SyntheticDefinitionDraftBehaviorEvaluator(
    DefinitionDraftSyntheticOfflineLanguageModel offlineModel,
    IModelCatalog catalog,
    IToolConfigurationGate configurationGate) : IDefinitionDraftSyntheticBehaviorEvaluator
{
    private const int MaxToolRounds = 3;

    public async ValueTask<DefinitionDraftSyntheticBehaviorResult> EvaluateAsync(
        AgentDefinitionCandidate candidate,
        IReadOnlyList<DefinitionDraftSyntheticResourceSnapshot> resources,
        DefinitionEvaluationScenario scenario,
        CancellationToken cancellationToken = default)
    {
        var definition = candidate.ToPublished(1);
        var selection = SessionModelBinder.PinDefault(catalog, definition);
        var modelSelection = $"synthetic-offline/scripted/{selection.CatalogKey}";
        var tools = ToolCatalog.For(definition, context: null, configurationGate);
        var messages = new List<ModelMessage>
        {
            new(ModelRole.System, PromptContextBuilder.BuildIdentitySystem(definition, definition.Identity)),
            new(ModelRole.System, DefinitionEvaluationHarness.BuildDirective(scenario)),
            new(ModelRole.System, DefinitionEvaluationHarness.BuildResourceManifest(resources)),
            new(ModelRole.User, scenario.Prompt)
        };

        var toolObservations = new List<DefinitionDraftSyntheticToolObservation>();
        string? assistantText = null;
        for (var round = 0; round < MaxToolRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = new ModelRequest(Guid.NewGuid(), messages, Tools: tools);
            ModelStopReason? stop = null;
            await foreach (var eventItem in offlineModel.GenerateAsync(request, cancellationToken).ConfigureAwait(false))
            {
                switch (eventItem)
                {
                    case ModelTextDelta delta:
                        assistantText = (assistantText ?? string.Empty) + delta.Text;
                        break;
                    case ModelToolCallEvent toolCall:
                        var policy = ToolPolicy.EvaluateExecution(definition, toolCall.Call.Name, configurationGate);
                        toolObservations.Add(new DefinitionDraftSyntheticToolObservation(toolCall.Call.Name, policy));
                        messages.Add(new ModelMessage(ModelRole.Assistant, string.Empty, ToolCalls: [toolCall.Call]));
                        messages.Add(new ModelMessage(
                            ModelRole.Tool,
                            $"Synthetic evaluation stub result for {toolCall.Call.Name}."));
                        break;
                    case ModelCompleted completed:
                        stop = completed.Reason;
                        break;
                    case ModelFailed failed:
                        return new DefinitionDraftSyntheticBehaviorResult(
                            "synthetic-offline",
                            modelSelection,
                            true,
                            toolObservations,
                            assistantText ?? failed.Failure.SafeMessage);
                }
            }

            if (stop != ModelStopReason.ToolCalls)
            {
                break;
            }
        }

        return new DefinitionDraftSyntheticBehaviorResult(
            "synthetic-offline",
            modelSelection,
            true,
            toolObservations,
            assistantText);
    }
}
