using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

public sealed class SqliteAgentDefinitionResourceAdminStore(
    IDbContextFactory<AgentCoreDbContext> contexts,
    IDefinitionResourceContentStore content,
    IIdGenerator ids) : IAgentDefinitionResourceAdminStore
{
    public async ValueTask<IReadOnlyList<AgentDefinitionDraftResource>> ListDraftResourcesAsync(
        Guid draftId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.AgentDefinitionDraftResources.AsNoTracking()
            .Where(row => row.DraftId == draftId.ToString("D"))
            .OrderBy(row => row.LogicalPath)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(DefinitionResourcePersistence.MapDraftResource).ToArray();
    }

    public async ValueTask<AgentDefinitionDraftResource> UpsertDraftResourceAsync(
        AgentDefinitionDraftResourceUpsert upsert,
        CancellationToken cancellationToken = default)
    {
        var logicalPath = DefinitionResourcePolicies.NormalizeLogicalPath(upsert.LogicalPath);
        var mediaType = DefinitionResourcePolicies.NormalizeMediaType(upsert.MediaType);
        await DefinitionResourceStoredContent.RequireForBindingAsync(
            content,
            upsert.ContentSha256,
            upsert.ByteLength,
            mediaType,
            cancellationToken).ConfigureAwait(false);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var draftKey = upsert.DraftId.ToString("D");
        var rows = await db.AgentDefinitionDraftResources
            .Where(row => row.DraftId == draftKey)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var aggregate = rows.Sum(row => row.ByteLength);
        var replacing = upsert.ResourceId is Guid resourceId
            && rows.Any(row => row.ResourceId == resourceId.ToString("D"));
        var replacingBytes = replacing
            ? rows.First(row => row.ResourceId == upsert.ResourceId!.Value.ToString("D")).ByteLength
            : 0;
        DefinitionResourcePolicies.ValidateAggregateSize(
            aggregate - replacingBytes,
            upsert.ByteLength,
            rows.Count,
            replacing);
        if (rows.Any(row => row.LogicalPath == logicalPath
            && (!replacing || row.ResourceId != upsert.ResourceId!.Value.ToString("D"))))
        {
            throw AgentCoreErrors.Conflict("logicalPath is already bound on this draft.");
        }

        var draftRow = await db.AgentDefinitionDrafts
            .SingleOrDefaultAsync(item => item.DraftId == draftKey, cancellationToken)
            .ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Draft was not found.");
        if (draftRow.Revision != upsert.ExpectedDraftRevision)
        {
            throw AgentCoreErrors.Conflict("Draft revision is stale.");
        }

        draftRow.Revision += 1;
        draftRow.UpdatedAtUtc = upsert.UpdatedAt.ToUnixTimeMilliseconds();

        var resource = new AgentDefinitionDraftResource(
            upsert.ResourceId ?? ids.NewId(),
            upsert.DraftId,
            logicalPath,
            upsert.Kind,
            mediaType,
            upsert.ContentSha256,
            upsert.ByteLength,
            upsert.UpdatedAt);
        var mapped = DefinitionResourcePersistence.MapDraftResource(resource);
        if (replacing)
        {
            var existing = rows.First(row => row.ResourceId == upsert.ResourceId!.Value.ToString("D"));
            db.AgentDefinitionDraftResources.Attach(existing);
            existing.LogicalPath = mapped.LogicalPath;
            existing.Kind = mapped.Kind;
            existing.MediaType = mapped.MediaType;
            existing.ContentSha256 = mapped.ContentSha256;
            existing.ByteLength = mapped.ByteLength;
            existing.UpdatedAtUtc = mapped.UpdatedAtUtc;
        }
        else
        {
            db.AgentDefinitionDraftResources.Add(mapped);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return resource;
    }

    public async ValueTask<AgentDefinitionDraftResource> RemoveDraftResourceAsync(
        AgentDefinitionDraftResourceRemove remove,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.AgentDefinitionDraftResources
            .SingleOrDefaultAsync(
                item => item.DraftId == remove.DraftId.ToString("D")
                    && item.ResourceId == remove.ResourceId.ToString("D"),
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Draft resource was not found.");

        var draftRow = await db.AgentDefinitionDrafts
            .SingleOrDefaultAsync(item => item.DraftId == remove.DraftId.ToString("D"), cancellationToken)
            .ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Draft was not found.");
        if (draftRow.Revision != remove.ExpectedDraftRevision)
        {
            throw AgentCoreErrors.Conflict("Draft revision is stale.");
        }

        draftRow.Revision += 1;
        draftRow.UpdatedAtUtc = remove.UpdatedAt.ToUnixTimeMilliseconds();
        db.AgentDefinitionDraftResources.Remove(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return DefinitionResourcePersistence.MapDraftResource(row);
    }

    public async ValueTask<byte[]?> ReadDraftResourceContentAsync(
        Guid draftId,
        Guid resourceId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.AgentDefinitionDraftResources.AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.DraftId == draftId.ToString("D") && item.ResourceId == resourceId.ToString("D"),
                cancellationToken)
            .ConfigureAwait(false);
        return row is null
            ? null
            : await content.ReadAsync(row.ContentSha256, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<AgentDefinitionPublicationResource>> ListPublicationResourcesAsync(
        string definitionId,
        int version,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.AgentDefinitionPublicationResources.AsNoTracking()
            .Where(row => row.DefinitionId == definitionId && row.Version == version)
            .OrderBy(row => row.LogicalPath)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(DefinitionResourcePersistence.MapPublicationResource).ToArray();
    }

    public async ValueTask<byte[]?> ReadPublicationResourceContentAsync(
        string definitionId,
        int version,
        Guid resourceId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.AgentDefinitionPublicationResources.AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.DefinitionId == definitionId
                    && item.Version == version
                    && item.ResourceId == resourceId.ToString("D"),
                cancellationToken)
            .ConfigureAwait(false);
        return row is null
            ? null
            : await content.ReadAsync(row.ContentSha256, cancellationToken).ConfigureAwait(false);
    }
}
