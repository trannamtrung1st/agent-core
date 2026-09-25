using System.Text.RegularExpressions;
using AgentCore.Application.Sessions;

namespace AgentCore.Application.Admin;

internal static class DefinitionEvaluationScenarioValidator
{
    private static readonly Regex ScenarioIdPattern = new("^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal static void Validate(DefinitionEvaluationScenarioUpsert upsert)
    {
        if (string.IsNullOrWhiteSpace(upsert.ScenarioId)
            || upsert.ScenarioId.Length > 64
            || !ScenarioIdPattern.IsMatch(upsert.ScenarioId))
        {
            throw AgentCoreErrors.Validation("scenarioId must be 1..64 lowercase letters, digits, or hyphens.");
        }

        if (string.IsNullOrWhiteSpace(upsert.Title) || upsert.Title.Length > 128)
        {
            throw AgentCoreErrors.Validation("title must be 1..128 characters.");
        }

        if (upsert.Prompt.Length > 2_000)
        {
            throw AgentCoreErrors.Validation("prompt must be at most 2000 characters.");
        }

        if (!Enum.IsDefined(upsert.RequirementLevel))
        {
            throw AgentCoreErrors.Validation("requirementLevel must be Required or Advisory.");
        }

        if (!Enum.IsDefined(upsert.CheckType))
        {
            throw AgentCoreErrors.Validation("checkType is not supported.");
        }

        if (upsert.CheckType is DefinitionEvaluationCheckType.ToolOffered or DefinitionEvaluationCheckType.ToolNotOffered)
        {
            if (string.IsNullOrWhiteSpace(upsert.ToolName) || upsert.ToolName.Length > 128)
            {
                throw AgentCoreErrors.Validation("toolName is required and must be at most 128 characters for tool checks.");
            }
        }
    }
}
