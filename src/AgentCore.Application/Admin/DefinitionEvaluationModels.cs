namespace AgentCore.Application.Admin;

public enum DefinitionEvaluationCheckType
{
    ToolOffered,
    ToolNotOffered
}

public enum DefinitionEvaluationRequirementLevel
{
    Required,
    Advisory
}

public sealed record DefinitionEvaluationScenario(
    Guid DraftId,
    string ScenarioId,
    int ScenarioVersion,
    string Title,
    string Prompt,
    DefinitionEvaluationRequirementLevel RequirementLevel,
    DefinitionEvaluationCheckType CheckType,
    string? ToolName,
    DateTimeOffset UpdatedAt);

public sealed record DefinitionEvaluationScenarioUpsert(
    Guid DraftId,
    string ScenarioId,
    string Title,
    string Prompt,
    DefinitionEvaluationRequirementLevel RequirementLevel,
    DefinitionEvaluationCheckType CheckType,
    string? ToolName,
    DateTimeOffset UpdatedAt);

public sealed record DefinitionEvaluationResult(
    Guid DraftId,
    long DraftRevision,
    string ConfigurationFingerprint,
    string ScenarioId,
    int ScenarioVersion,
    string RuntimeKind,
    bool Passed,
    IReadOnlyList<string> Findings,
    DateTimeOffset RecordedAt);
