namespace AgentCore.Application.Admin;

public enum DefinitionDiffChangeKind
{
    Unchanged,
    Added,
    Removed,
    Modified
}

public sealed record DefinitionDiffSection(
    string SectionId,
    string Label,
    DefinitionDiffChangeKind ChangeKind,
    string? BeforeSummary,
    string? AfterSummary);

public sealed record DefinitionDraftDiffResult(
    Guid DraftId,
    long DraftRevision,
    string BaselineKind,
    int? BaselineVersion,
    IReadOnlyList<DefinitionDiffSection> Sections);
