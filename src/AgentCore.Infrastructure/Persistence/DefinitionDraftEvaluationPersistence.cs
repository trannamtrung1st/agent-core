using System.Text.Json;
using AgentCore.Application.Admin;

namespace AgentCore.Infrastructure.Persistence;

internal static class DefinitionDraftEvaluationPersistence
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static DefinitionEvaluationScenario MapScenario(AgentDefinitionDraftEvaluationScenarioRecord row) =>
        new(
            Guid.Parse(row.DraftId),
            row.ScenarioId,
            row.ScenarioVersion,
            row.Title,
            row.Prompt,
            (DefinitionEvaluationRequirementLevel)row.RequirementLevel,
            (DefinitionEvaluationCheckType)row.CheckType,
            string.IsNullOrEmpty(row.ToolName) ? null : row.ToolName,
            DateTimeOffset.FromUnixTimeMilliseconds(row.UpdatedAtUtc));

    internal static AgentDefinitionDraftEvaluationScenarioRecord MapScenario(
        DefinitionEvaluationScenario scenario)
    {
        return new AgentDefinitionDraftEvaluationScenarioRecord
        {
            DraftId = scenario.DraftId.ToString("D"),
            ScenarioId = scenario.ScenarioId,
            ScenarioVersion = scenario.ScenarioVersion,
            Title = scenario.Title,
            Prompt = scenario.Prompt,
            RequirementLevel = (int)scenario.RequirementLevel,
            CheckType = (int)scenario.CheckType,
            ToolName = scenario.ToolName ?? "",
            UpdatedAtUtc = scenario.UpdatedAt.ToUnixTimeMilliseconds()
        };
    }

    internal static DefinitionEvaluationResult MapResult(AgentDefinitionDraftEvaluationResultRecord row) =>
        new(
            Guid.Parse(row.DraftId),
            row.DraftRevision,
            row.ConfigurationFingerprint,
            row.ScenarioId,
            row.ScenarioVersion,
            row.RuntimeKind,
            row.Passed,
            DeserializeFindings(row.FindingsJson),
            DateTimeOffset.FromUnixTimeMilliseconds(row.RecordedAtUtc));

    internal static AgentDefinitionDraftEvaluationResultRecord MapResult(DefinitionEvaluationResult result, Guid resultId)
    {
        return new AgentDefinitionDraftEvaluationResultRecord
        {
            ResultId = resultId.ToString("D"),
            DraftId = result.DraftId.ToString("D"),
            DraftRevision = result.DraftRevision,
            ConfigurationFingerprint = result.ConfigurationFingerprint,
            ScenarioId = result.ScenarioId,
            ScenarioVersion = result.ScenarioVersion,
            RuntimeKind = result.RuntimeKind,
            Passed = result.Passed,
            FindingsJson = SerializeFindings(result.Findings),
            RecordedAtUtc = result.RecordedAt.ToUnixTimeMilliseconds()
        };
    }

    internal static string SerializeFindings(IReadOnlyList<string> findings) =>
        JsonSerializer.Serialize(findings, JsonOptions);

    internal static string[] DeserializeFindings(string json) =>
        JsonSerializer.Deserialize<string[]>(json, JsonOptions) ?? [];
}
