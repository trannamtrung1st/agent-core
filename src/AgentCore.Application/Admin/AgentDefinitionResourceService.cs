using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Admin;

public sealed class AgentDefinitionResourceService(
    IAgentDefinitionAdminStore drafts,
    IAgentDefinitionResourceAdminStore resources,
    IDefinitionResourceContentStore content,
    TimeProvider time)
{
    public async ValueTask<IReadOnlyList<AgentDefinitionDraftResource>> ListDraftResourcesAsync(
        Guid draftId,
        CancellationToken cancellationToken = default)
    {
        await RequireDraftAsync(draftId, cancellationToken).ConfigureAwait(false);
        return await resources.ListDraftResourcesAsync(draftId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<DefinitionResourceContentStored> StoreDraftContentAsync(
        Guid draftId,
        string mediaType,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        await RequireDraftAsync(draftId, cancellationToken).ConfigureAwait(false);
        var normalizedMediaType = DefinitionResourcePolicies.NormalizeMediaType(mediaType);
        if (payload.Length == 0)
        {
            throw AgentCoreErrors.Validation("Resource content must not be empty.");
        }

        DefinitionResourcePolicies.ValidateContentSize(payload.Length);
        DefinitionResourcePolicies.RejectSecretsInTextualContent(normalizedMediaType, payload.Span);
        var hash = DefinitionResourceContentHasher.ComputeSha256Hex(payload.Span);
        await content.StoreVerifiedAsync(hash, payload.ToArray(), cancellationToken).ConfigureAwait(false);
        return new DefinitionResourceContentStored(hash, payload.Length, normalizedMediaType);
    }

    public async ValueTask<AgentDefinitionDraftResource> UpsertDraftResourceAsync(
        Guid draftId,
        long expectedRevision,
        Guid? resourceId,
        string logicalPath,
        AgentDefinitionResourceKind kind,
        string mediaType,
        string contentSha256,
        long byteLength,
        CancellationToken cancellationToken = default)
    {
        await RequireDraftAsync(draftId, cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        return await resources.UpsertDraftResourceAsync(
            new AgentDefinitionDraftResourceUpsert(
                draftId,
                expectedRevision,
                resourceId,
                logicalPath,
                kind,
                mediaType,
                contentSha256,
                byteLength,
                now),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<AgentDefinitionDraftResource> RemoveDraftResourceAsync(
        Guid draftId,
        long expectedRevision,
        Guid resourceId,
        CancellationToken cancellationToken = default)
    {
        await RequireDraftAsync(draftId, cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        return await resources.RemoveDraftResourceAsync(
            new AgentDefinitionDraftResourceRemove(draftId, expectedRevision, resourceId, now),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<byte[]?> ReadDraftResourceContentAsync(
        Guid draftId,
        Guid resourceId,
        CancellationToken cancellationToken = default)
    {
        await RequireDraftAsync(draftId, cancellationToken).ConfigureAwait(false);
        return await resources.ReadDraftResourceContentAsync(draftId, resourceId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<AgentDefinitionPublicationResource>> ListPublicationResourcesAsync(
        string definitionId,
        int version,
        CancellationToken cancellationToken = default)
    {
        _ = await drafts.GetPublicationAsync(definitionId, version, cancellationToken).ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Publication was not found.");
        return await resources.ListPublicationResourcesAsync(definitionId, version, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<byte[]?> ReadPublicationResourceContentAsync(
        string definitionId,
        int version,
        Guid resourceId,
        CancellationToken cancellationToken = default)
    {
        _ = await drafts.GetPublicationAsync(definitionId, version, cancellationToken).ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Publication was not found.");
        return await resources.ReadPublicationResourceContentAsync(definitionId, version, resourceId, cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask RequireDraftAsync(Guid draftId, CancellationToken cancellationToken)
    {
        _ = await drafts.GetDraftAsync(draftId, cancellationToken).ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Definition draft was not found.");
    }
}

public sealed record DefinitionResourceContentStored(string ContentSha256, long ByteLength, string MediaType);
