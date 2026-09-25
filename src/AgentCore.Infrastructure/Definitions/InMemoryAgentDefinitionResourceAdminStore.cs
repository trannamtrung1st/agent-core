using System.Collections.Concurrent;
using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;

namespace AgentCore.Infrastructure.Definitions;

public sealed class InMemoryAgentDefinitionResourceAdminStore(
    InMemoryAgentDefinitionAdminStore drafts,
    IDefinitionResourceContentStore content,
    IIdGenerator ids) : IAgentDefinitionResourceAdminStore
{
    private readonly ConcurrentDictionary<Guid, List<AgentDefinitionDraftResource>> _draftResources = new();
    private readonly ConcurrentDictionary<(string DefinitionId, int Version), List<AgentDefinitionPublicationResource>> _publicationResources = new();

    internal void EnsureReadyForPublication(Guid draftId)
    {
        if (!_draftResources.TryGetValue(draftId, out var draftList) || draftList.Count == 0)
        {
            return;
        }

        foreach (var item in draftList)
        {
            DefinitionResourceStoredContent.RequireForBindingAsync(
                    content,
                    item.ContentSha256,
                    item.ByteLength,
                    item.MediaType,
                    CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }
    }

    internal void SnapshotPublicationOnPublish(Guid draftId, string definitionId, int version)
    {
        EnsureReadyForPublication(draftId);
        if (!_draftResources.TryGetValue(draftId, out var draftList))
        {
            _publicationResources[(definitionId, version)] = [];
            return;
        }

        var bindings = draftList
            .Select(item => new AgentDefinitionPublicationResource(
                definitionId,
                version,
                item.ResourceId,
                item.LogicalPath,
                item.Kind,
                item.MediaType,
                item.ContentSha256,
                item.ByteLength))
            .ToList();
        _publicationResources[(definitionId, version)] = bindings;
    }

    public ValueTask<IReadOnlyList<AgentDefinitionDraftResource>> ListDraftResourcesAsync(
        Guid draftId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(SnapshotDraftResources(draftId));
    }

    public async ValueTask<AgentDefinitionDraftResource> UpsertDraftResourceAsync(
        AgentDefinitionDraftResourceUpsert upsert,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var logicalPath = DefinitionResourcePolicies.NormalizeLogicalPath(upsert.LogicalPath);
        var mediaType = DefinitionResourcePolicies.NormalizeMediaType(upsert.MediaType);
        await DefinitionResourceStoredContent.RequireForBindingAsync(
            content,
            upsert.ContentSha256,
            upsert.ByteLength,
            mediaType,
            cancellationToken).ConfigureAwait(false);
        var gate = DefinitionDraftLockRegistry.For(upsert.DraftId);
        lock (gate)
        {
            var list = _draftResources.GetOrAdd(upsert.DraftId, static _ => []);
            var aggregate = list.Sum(item => item.ByteLength);
            var replacing = upsert.ResourceId is Guid resourceId
                && list.Any(item => item.ResourceId == resourceId);
            var replacingBytes = replacing
                ? list.First(item => item.ResourceId == upsert.ResourceId!.Value).ByteLength
                : 0;
            DefinitionResourcePolicies.ValidateAggregateSize(
                aggregate - replacingBytes,
                upsert.ByteLength,
                list.Count,
                replacing);

            if (list.Any(item => string.Equals(item.LogicalPath, logicalPath, StringComparison.Ordinal)
                && (!replacing || item.ResourceId != upsert.ResourceId)))
            {
                throw AgentCoreErrors.Conflict("logicalPath is already bound on this draft.");
            }

            _ = drafts.BumpDraftRevisionAsync(
                new AgentDefinitionDraftRevisionBump(upsert.DraftId, upsert.ExpectedDraftRevision, upsert.UpdatedAt),
                cancellationToken).AsTask().GetAwaiter().GetResult();

            var resource = new AgentDefinitionDraftResource(
                upsert.ResourceId ?? ids.NewId(),
                upsert.DraftId,
                logicalPath,
                upsert.Kind,
                mediaType,
                upsert.ContentSha256,
                upsert.ByteLength,
                upsert.UpdatedAt);

            if (replacing)
            {
                var index = list.FindIndex(item => item.ResourceId == upsert.ResourceId);
                list[index] = resource;
            }
            else
            {
                list.Add(resource);
            }

            return resource;
        }
    }

    public async ValueTask<AgentDefinitionDraftResource> RemoveDraftResourceAsync(
        AgentDefinitionDraftResourceRemove remove,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var gate = DefinitionDraftLockRegistry.For(remove.DraftId);
        lock (gate)
        {
            if (!_draftResources.TryGetValue(remove.DraftId, out var list))
            {
                throw AgentCoreErrors.NotFound("Draft resource was not found.");
            }

            var index = list.FindIndex(item => item.ResourceId == remove.ResourceId);
            if (index < 0)
            {
                throw AgentCoreErrors.NotFound("Draft resource was not found.");
            }

            _ = drafts.BumpDraftRevisionAsync(
                new AgentDefinitionDraftRevisionBump(remove.DraftId, remove.ExpectedDraftRevision, remove.UpdatedAt),
                cancellationToken).AsTask().GetAwaiter().GetResult();

            var removed = list[index];
            list.RemoveAt(index);
            return removed;
        }
    }

    public async ValueTask<byte[]?> ReadDraftResourceContentAsync(
        Guid draftId,
        Guid resourceId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resource = FindDraftResource(draftId, resourceId);
        return resource is null
            ? null
            : await content.ReadAsync(resource.ContentSha256, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<IReadOnlyList<AgentDefinitionPublicationResource>> ListPublicationResourcesAsync(
        string definitionId,
        int version,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_publicationResources.TryGetValue((definitionId, version), out var items))
        {
            return ValueTask.FromResult<IReadOnlyList<AgentDefinitionPublicationResource>>([]);
        }

        return ValueTask.FromResult<IReadOnlyList<AgentDefinitionPublicationResource>>(items.OrderBy(item => item.LogicalPath, StringComparer.Ordinal).ToArray());
    }

    public async ValueTask<byte[]?> ReadPublicationResourceContentAsync(
        string definitionId,
        int version,
        Guid resourceId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_publicationResources.TryGetValue((definitionId, version), out var items))
        {
            return null;
        }

        var resource = items.FirstOrDefault(item => item.ResourceId == resourceId);
        return resource is null
            ? null
            : await content.ReadAsync(resource.ContentSha256, cancellationToken).ConfigureAwait(false);
    }

    private IReadOnlyList<AgentDefinitionDraftResource> SnapshotDraftResources(Guid draftId)
    {
        var gate = DefinitionDraftLockRegistry.For(draftId);
        lock (gate)
        {
            if (!_draftResources.TryGetValue(draftId, out var items))
            {
                return [];
            }

            return items
                .OrderBy(item => item.LogicalPath, StringComparer.Ordinal)
                .Select(item => item)
                .ToArray();
        }
    }

    private AgentDefinitionDraftResource? FindDraftResource(Guid draftId, Guid resourceId)
    {
        var gate = DefinitionDraftLockRegistry.For(draftId);
        lock (gate)
        {
            if (!_draftResources.TryGetValue(draftId, out var items))
            {
                return null;
            }

            return items.FirstOrDefault(item => item.ResourceId == resourceId);
        }
    }

}
