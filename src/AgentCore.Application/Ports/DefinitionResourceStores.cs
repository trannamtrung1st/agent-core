using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Ports;

public interface IDefinitionResourceContentStore
{
    ValueTask StoreVerifiedAsync(string contentSha256, byte[] content, CancellationToken cancellationToken = default);

    ValueTask<byte[]?> ReadAsync(string contentSha256, CancellationToken cancellationToken = default);
}

public interface IAgentDefinitionResourceAdminStore
{
    ValueTask<IReadOnlyList<AgentDefinitionDraftResource>> ListDraftResourcesAsync(
        Guid draftId,
        CancellationToken cancellationToken = default);

    ValueTask<AgentDefinitionDraftResource> UpsertDraftResourceAsync(
        AgentDefinitionDraftResourceUpsert upsert,
        CancellationToken cancellationToken = default);

    ValueTask<AgentDefinitionDraftResource> RemoveDraftResourceAsync(
        AgentDefinitionDraftResourceRemove remove,
        CancellationToken cancellationToken = default);

    ValueTask<byte[]?> ReadDraftResourceContentAsync(
        Guid draftId,
        Guid resourceId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<AgentDefinitionPublicationResource>> ListPublicationResourcesAsync(
        string definitionId,
        int version,
        CancellationToken cancellationToken = default);

    ValueTask<byte[]?> ReadPublicationResourceContentAsync(
        string definitionId,
        int version,
        Guid resourceId,
        CancellationToken cancellationToken = default);
}

public sealed record AgentDefinitionDraftResourceUpsert(
    Guid DraftId,
    long ExpectedDraftRevision,
    Guid? ResourceId,
    string LogicalPath,
    AgentDefinitionResourceKind Kind,
    string MediaType,
    string ContentSha256,
    long ByteLength,
    DateTimeOffset UpdatedAt);

public sealed record AgentDefinitionDraftResourceRemove(
    Guid DraftId,
    long ExpectedDraftRevision,
    Guid ResourceId,
    DateTimeOffset UpdatedAt);
