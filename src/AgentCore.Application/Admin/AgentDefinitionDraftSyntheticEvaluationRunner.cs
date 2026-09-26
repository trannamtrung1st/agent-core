using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Admin;

internal static class AgentDefinitionDraftSyntheticEvaluationRunner
{
    internal static (bool Passed, IReadOnlyList<string> Findings) Run(
        AgentDefinitionCandidate candidate,
        IReadOnlyList<AgentDefinitionDraftResource> draftResources,
        DefinitionEvaluationScenario scenario,
        IToolConfigurationGate configurationGate)
    {
        return scenario.CheckType switch
        {
            DefinitionEvaluationCheckType.ToolOffered or DefinitionEvaluationCheckType.ToolNotOffered
                or DefinitionEvaluationCheckType.ExternalActionDenied =>
                RunToolCheck(candidate, scenario, configurationGate),
            DefinitionEvaluationCheckType.ResourceBound =>
                RunResourceBoundCheck(draftResources, scenario),
            DefinitionEvaluationCheckType.TriggerSchedulePermitted =>
                RunTriggerScheduleCheck(candidate),
            _ => (false, ["Unsupported evaluation check type."])
        };
    }

    private static (bool Passed, IReadOnlyList<string> Findings) RunToolCheck(
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
            DefinitionEvaluationCheckType.ExternalActionDenied when !offered =>
                (true, []),
            DefinitionEvaluationCheckType.ExternalActionDenied =>
                (false, [$"External action '{scenario.ToolName}' must not be offered for this draft."]),
            _ => (false, ["Unsupported evaluation check type."])
        };
    }

    private static (bool Passed, IReadOnlyList<string> Findings) RunResourceBoundCheck(
        IReadOnlyList<AgentDefinitionDraftResource> draftResources,
        DefinitionEvaluationScenario scenario)
    {
        if (string.IsNullOrWhiteSpace(scenario.ToolName))
        {
            return (false, ["toolName must carry the bound resource logical path for ResourceBound checks."]);
        }

        var bound = draftResources.Any(resource =>
            string.Equals(resource.LogicalPath, scenario.ToolName, StringComparison.Ordinal));
        return bound
            ? (true, [])
            : (false, [$"Draft has no resource binding for path '{scenario.ToolName}'."]);
    }

    private static (bool Passed, IReadOnlyList<string> Findings) RunTriggerScheduleCheck(
        AgentDefinitionCandidate candidate)
    {
        var policy = candidate.TriggerPolicy;
        if (policy is { Enabled: true, AllowUserScheduling: true, AllowOneShot: true })
        {
            return (true, []);
        }

        return (false, ["Trigger policy does not permit user scheduling for this draft."]);
    }
}
