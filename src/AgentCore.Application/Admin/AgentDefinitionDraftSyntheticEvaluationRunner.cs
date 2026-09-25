using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Admin;

internal static class AgentDefinitionDraftSyntheticEvaluationRunner
{
    internal static (bool Passed, IReadOnlyList<string> Findings) Run(
        AgentDefinitionCandidate candidate,
        DefinitionEvaluationScenario scenario,
        IToolConfigurationGate configurationGate)
    {
        if (string.IsNullOrWhiteSpace(scenario.ToolName))
        {
            return (false, ["toolName is required for this scenario check."]);
        }

        var definition = candidate.ToPublished(1);
        var offered = ToolPolicy.EvaluateExecution(
            definition,
            scenario.ToolName,
            configurationGate) != ToolPolicyDecision.Deny;
        return scenario.CheckType switch
        {
            DefinitionEvaluationCheckType.ToolOffered when offered =>
                (true, []),
            DefinitionEvaluationCheckType.ToolOffered =>
                (false, [$"Tool '{scenario.ToolName}' is not offered for this draft."]),
            DefinitionEvaluationCheckType.ToolNotOffered when !offered =>
                (true, []),
            DefinitionEvaluationCheckType.ToolNotOffered =>
                (false, [$"Tool '{scenario.ToolName}' must not be offered for this draft."]),
            _ => (false, ["Unsupported evaluation check type."])
        };
    }
}
