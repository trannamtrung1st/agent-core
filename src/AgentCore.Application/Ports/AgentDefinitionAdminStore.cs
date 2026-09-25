using AgentCore.Application.Admin;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Ports;

public interface IAgentDefinitionAdminStore
{
    ValueTask<IReadOnlyList<AgentDefinitionDraftSummary>> ListDraftsAsync(
        CancellationToken cancellationToken = default);

    ValueTask<AgentDefinitionDraft?> GetDraftAsync(Guid draftId, CancellationToken cancellationToken = default);

    ValueTask<AgentDefinitionDraft> CreateDraftAsync(
        AgentDefinitionDraftCreate create,
        CancellationToken cancellationToken = default);

    ValueTask<AgentDefinitionDraft> UpdateDraftAsync(
        AgentDefinitionDraftUpdate update,
        CancellationToken cancellationToken = default);

    ValueTask<AgentDefinitionDraft> BumpDraftRevisionAsync(
        AgentDefinitionDraftRevisionBump bump,
        CancellationToken cancellationToken = default);

    ValueTask<AgentDefinitionPublication> PublishDraftAsync(
        AgentDefinitionDraftPublish publish,
        CancellationToken cancellationToken = default);

    ValueTask<AgentDefinitionPublication?> GetPublicationAsync(
        string definitionId,
        int version,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<AgentDefinitionPublicationSummary>> ListPublicationsAsync(
        string? definitionId = null,
        CancellationToken cancellationToken = default);

    ValueTask<AgentDefinitionPublication> DeprecatePublicationAsync(
        AgentDefinitionPublicationDeprecate deprecate,
        CancellationToken cancellationToken = default);
}

public sealed record AgentDefinitionDraftCreate(
    string DefinitionId,
    AgentDefinitionCandidate Candidate,
    DefinitionDraftSourceKind SourceKind,
    int? SourceVersion,
    DateTimeOffset CreatedAt);

public sealed record AgentDefinitionDraftUpdate(
    Guid DraftId,
    long ExpectedRevision,
    AgentDefinitionCandidate Candidate,
    DateTimeOffset UpdatedAt);

public sealed record AgentDefinitionDraftRevisionBump(
    Guid DraftId,
    long ExpectedRevision,
    DateTimeOffset UpdatedAt);

public sealed record AgentDefinitionDraftPublish(
    Guid DraftId,
    long ExpectedRevision,
    IReadOnlyCollection<int> OccupiedVersions,
    DateTimeOffset PublishedAt,
    Guid OperationId = default,
    AdminEventActorKind ActorKind = AdminEventActorKind.LocalOwner,
    IReadOnlyList<string>? ChangedSectionIds = null);

public sealed record AgentDefinitionPublicationDeprecate(
    string DefinitionId,
    int Version,
    long ExpectedMetadataRevision,
    DateTimeOffset UpdatedAt);
