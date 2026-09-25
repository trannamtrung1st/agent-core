namespace AgentCore.Domain.Definitions;

public enum DefinitionDraftSourceKind
{
    New,
    ForkBuiltIn,
    ForkDurable
}

public enum DefinitionPublicationStatus
{
    Active,
    Deprecated
}

public sealed record AgentDefinitionDraft(
    Guid DraftId,
    string DefinitionId,
    long Revision,
    AgentDefinitionCandidate Candidate,
    DefinitionDraftSourceKind SourceKind,
    int? SourceVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record AgentDefinitionDraftSummary(
    Guid DraftId,
    string DefinitionId,
    long Revision,
    DefinitionDraftSourceKind SourceKind,
    int? SourceVersion,
    DateTimeOffset UpdatedAt);

public sealed record AgentDefinitionPublication(
    string DefinitionId,
    int Version,
    AgentDefinition Payload,
    long SourceDraftRevision,
    DefinitionPublicationStatus Status,
    long MetadataRevision,
    DateTimeOffset PublishedAt);

public sealed record AgentDefinitionPublicationSummary(
    string DefinitionId,
    int Version,
    DefinitionPublicationStatus Status,
    DateTimeOffset PublishedAt);
