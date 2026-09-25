namespace AgentCore.Application.Admin;

public enum DefinitionValidationSeverity
{
    Blocking,
    Advisory
}

public sealed record DefinitionValidationFinding(
    string Field,
    string Code,
    string Message,
    DefinitionValidationSeverity Severity);

public sealed record DefinitionValidationResult(
    Guid DraftId,
    long DraftRevision,
    bool HasBlockingFindings,
    IReadOnlyList<DefinitionValidationFinding> Findings);
