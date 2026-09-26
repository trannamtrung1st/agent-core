using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Admin;

public sealed class AgentDefinitionDraftSyntheticEvaluationRunner(
    IDefinitionDraftSyntheticBehaviorEvaluator behaviorEvaluator,
    IToolConfigurationGate configurationGate)
{
    public async ValueTask<(bool Passed, IReadOnlyList<string> Findings)> RunAsync(
        AgentDefinitionCandidate candidate,
        IReadOnlyList<DefinitionDraftSyntheticResourceSnapshot> resourceSnapshots,
        DefinitionEvaluationScenario scenario,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(scenario.Prompt))
        {
            return (false, ["prompt is required for Synthetic behavior evaluation."]);
        }

        var behavior = await behaviorEvaluator
            .EvaluateAsync(candidate, resourceSnapshots, scenario, cancellationToken)
            .ConfigureAwait(false);
        if (!behavior.PromptIncludedInRequest)
        {
            return (false, ["Synthetic evaluation did not include the scenario prompt in the runtime request."]);
        }

        return scenario.CheckType switch
        {
            DefinitionEvaluationCheckType.ToolOffered or DefinitionEvaluationCheckType.ToolNotOffered
                or DefinitionEvaluationCheckType.ExternalActionDenied =>
                EvaluateToolCheck(candidate, scenario, behavior),
            DefinitionEvaluationCheckType.ResourceBound =>
                EvaluateResourceBoundCheck(resourceSnapshots, scenario, behavior),
            DefinitionEvaluationCheckType.TriggerSchedulePermitted =>
                EvaluateTriggerScheduleCheck(candidate, behavior),
            _ => (false, ["Unsupported evaluation check type."])
        };
    }

    private (bool Passed, IReadOnlyList<string> Findings) EvaluateToolCheck(
        AgentDefinitionCandidate candidate,
        DefinitionEvaluationScenario scenario,
        DefinitionDraftSyntheticBehaviorResult behavior)
    {
        if (string.IsNullOrWhiteSpace(scenario.ToolName))
        {
            return (false, ["toolName is required for this scenario check."]);
        }

        var definition = candidate.ToPublished(1);
        var policy = ToolPolicy.EvaluateExecution(definition, scenario.ToolName, configurationGate);
        var observed = behavior.ToolObservations
            .Where(item => string.Equals(item.ToolName, scenario.ToolName, StringComparison.Ordinal))
            .ToArray();

        return scenario.CheckType switch
        {
            DefinitionEvaluationCheckType.ToolOffered when policy == ToolPolicyDecision.Deny =>
                (false, [$"Tool '{scenario.ToolName}' is not offered for this draft."]),
            DefinitionEvaluationCheckType.ToolOffered when observed.Any(item => item.PolicyDecision != ToolPolicyDecision.Deny) =>
                (true, []),
            DefinitionEvaluationCheckType.ToolOffered =>
                (false, [$"Synthetic runtime did not invoke offered tool '{scenario.ToolName}' for the scenario prompt."]),
            DefinitionEvaluationCheckType.ToolNotOffered when policy != ToolPolicyDecision.Deny =>
                (false, [$"Tool '{scenario.ToolName}' must not be offered for this draft."]),
            DefinitionEvaluationCheckType.ToolNotOffered when observed.Length == 0 =>
                (true, []),
            DefinitionEvaluationCheckType.ToolNotOffered when observed.All(item => item.PolicyDecision == ToolPolicyDecision.Deny) =>
                (true, []),
            DefinitionEvaluationCheckType.ToolNotOffered =>
                (false, [$"Synthetic runtime executed disallowed tool '{scenario.ToolName}'."]),
            DefinitionEvaluationCheckType.ExternalActionDenied when policy != ToolPolicyDecision.Deny =>
                (false, [$"External action '{scenario.ToolName}' must not be offered for this draft."]),
            DefinitionEvaluationCheckType.ExternalActionDenied when observed.Length == 0 =>
                (true, []),
            DefinitionEvaluationCheckType.ExternalActionDenied when observed.All(item => item.PolicyDecision == ToolPolicyDecision.Deny) =>
                (true, []),
            DefinitionEvaluationCheckType.ExternalActionDenied =>
                (false, [$"Synthetic runtime executed unauthorized external action '{scenario.ToolName}'."]),
            _ => (false, ["Unsupported evaluation check type."])
        };
    }

    private static (bool Passed, IReadOnlyList<string> Findings) EvaluateResourceBoundCheck(
        IReadOnlyList<DefinitionDraftSyntheticResourceSnapshot> resourceSnapshots,
        DefinitionEvaluationScenario scenario,
        DefinitionDraftSyntheticBehaviorResult behavior)
    {
        if (string.IsNullOrWhiteSpace(scenario.ToolName))
        {
            return (false, ["toolName must carry the bound resource logical path for ResourceBound checks."]);
        }

        var bound = resourceSnapshots.Any(resource =>
            string.Equals(resource.LogicalPath, scenario.ToolName, StringComparison.Ordinal));
        if (!bound)
        {
            return (false, [$"Draft has no resource binding for path '{scenario.ToolName}'."]);
        }

        var retrieveCalls = behavior.ToolObservations
            .Where(item => string.Equals(item.ToolName, ToolCatalog.KnowledgeRetrieve, StringComparison.Ordinal))
            .ToArray();
        if (retrieveCalls.Length == 0)
        {
            return (false, ["Synthetic runtime did not retrieve knowledge for the bound resource."]);
        }

        if (retrieveCalls.Any(item => item.PolicyDecision == ToolPolicyDecision.Deny))
        {
            return (false, ["Synthetic runtime was denied knowledge retrieval for the bound resource."]);
        }

        return (true, []);
    }

    private static (bool Passed, IReadOnlyList<string> Findings) EvaluateTriggerScheduleCheck(
        AgentDefinitionCandidate candidate,
        DefinitionDraftSyntheticBehaviorResult behavior)
    {
        var policy = candidate.TriggerPolicy;
        if (policy is not { Enabled: true, AllowUserScheduling: true, AllowOneShot: true })
        {
            return (false, ["Trigger policy does not permit user scheduling for this draft."]);
        }

        if (string.IsNullOrWhiteSpace(behavior.AssistantText)
            || !behavior.AssistantText.Contains("trigger scheduling permitted", StringComparison.OrdinalIgnoreCase))
        {
            return (false, ["Synthetic runtime did not confirm trigger scheduling for the scenario prompt."]);
        }

        return (true, []);
    }
}
