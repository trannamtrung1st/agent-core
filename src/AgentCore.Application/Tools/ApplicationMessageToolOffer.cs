using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tools;

internal static class ApplicationMessageToolOffer
{
    /// <summary>
    /// Narrows or extends the brain-authorized tool list for one model generation.
    /// Runtime never adds tools beyond <paramref name="authorizedTools"/> except
    /// <see cref="ToolCatalog.AppMessageSend"/> after intermediate messaging is allowed and budget remains.
    /// </summary>
    public static IReadOnlyList<ModelToolDefinition>? Apply(
        IReadOnlyList<ModelToolDefinition>? authorizedTools,
        bool intermediateMessagingAllowed,
        ApplicationMessageBudget budget,
        AgentDefinition definition,
        AgentTrigger trigger,
        IToolConfigurationGate configurationGate,
        bool modelSupportsTools)
    {
        if (!modelSupportsTools)
        {
            return null;
        }

        if (authorizedTools is not { Count: > 0 })
        {
            return authorizedTools;
        }

        var working = authorizedTools
            .Where(tool => (intermediateMessagingAllowed && budget.CanOfferTool)
                || !string.Equals(tool.Name, ToolCatalog.AppMessageSend, StringComparison.Ordinal))
            .ToList();

        if (intermediateMessagingAllowed
            && budget.CanOfferTool
            && !working.Any(tool => string.Equals(tool.Name, ToolCatalog.AppMessageSend, StringComparison.Ordinal))
            && ToolPolicy.EvaluateExecution(
                definition,
                ToolCatalog.AppMessageSend,
                configurationGate,
                admission: new ToolExecutionAdmission(false, trigger.Kind, true)) == ToolPolicyDecision.Allow
            && ToolRegistry.TryGet(ToolCatalog.AppMessageSend, out var descriptor))
        {
            working.Add(ApplicationMessageToolDescriptions.WithBudget(descriptor.ModelDefinition, budget));
        }
        else if (intermediateMessagingAllowed && budget.CanOfferTool)
        {
            for (var index = 0; index < working.Count; index++)
            {
                if (!string.Equals(working[index].Name, ToolCatalog.AppMessageSend, StringComparison.Ordinal))
                {
                    continue;
                }

                working[index] = ApplicationMessageToolDescriptions.WithBudget(working[index], budget);
                break;
            }
        }

        return working.Count == 0 ? null : working;
    }
}
