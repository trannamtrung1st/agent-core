using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tools;

internal static class ApplicationMessageToolOffer
{
    /// <summary>
    /// Narrows or extends the brain-authorized tool list for one model generation.
    /// Runtime never adds tools beyond <paramref name="authorizedTools"/> except
    /// <see cref="ToolCatalog.AppMessageSend"/> after intermediate messaging is allowed.
    /// </summary>
    public static IReadOnlyList<ModelToolDefinition>? Apply(
        IReadOnlyList<ModelToolDefinition>? authorizedTools,
        bool intermediateMessagingAllowed,
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
            .Where(tool => intermediateMessagingAllowed
                || !string.Equals(tool.Name, ToolCatalog.AppMessageSend, StringComparison.Ordinal))
            .ToList();

        if (intermediateMessagingAllowed
            && !working.Any(tool => string.Equals(tool.Name, ToolCatalog.AppMessageSend, StringComparison.Ordinal))
            && ToolPolicy.EvaluateExecution(
                definition,
                ToolCatalog.AppMessageSend,
                configurationGate,
                admission: new ToolExecutionAdmission(false, trigger.Kind, true)) == ToolPolicyDecision.Allow
            && ToolRegistry.TryGet(ToolCatalog.AppMessageSend, out var descriptor))
        {
            working.Add(descriptor.ModelDefinition);
        }

        return working.Count == 0 ? null : working;
    }
}
