using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

public sealed class SqliteAgentDefinitionAdminStore(
    IDbContextFactory<AgentCoreDbContext> contexts,
    IIdGenerator ids,
    IDefinitionResourceContentStore content) : IAgentDefinitionAdminStore
{
    public async ValueTask<IReadOnlyList<AgentDefinitionDraftSummary>> ListDraftsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.AgentDefinitionDrafts.AsNoTracking()
            .OrderBy(row => row.DefinitionId)
            .ThenByDescending(row => row.UpdatedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows
            .Select(row =>
            {
                var draft = AgentDefinitionAdminMapping.MapDraft(row);
                return new AgentDefinitionDraftSummary(
                    draft.DraftId,
                    draft.DefinitionId,
                    draft.Revision,
                    draft.SourceKind,
                    draft.SourceVersion,
                    draft.UpdatedAt);
            })
            .ToArray();
    }

    public async ValueTask<AgentDefinitionDraft?> GetDraftAsync(Guid draftId, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.AgentDefinitionDrafts.AsNoTracking()
            .SingleOrDefaultAsync(item => item.DraftId == draftId.ToString("D"), cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : AgentDefinitionAdminMapping.MapDraft(row);
    }

    public async ValueTask<AgentDefinitionDraft> CreateDraftAsync(
        AgentDefinitionDraftCreate create,
        CancellationToken cancellationToken = default)
    {
        AgentDefinitionValidator.ValidateCandidate(create.Candidate);
        if (!string.Equals(create.DefinitionId, create.Candidate.DefinitionId, StringComparison.Ordinal))
        {
            throw AgentCoreErrors.Validation("definitionId must match the candidate definitionId.");
        }

        var draft = new AgentDefinitionDraft(
            ids.NewId(),
            create.DefinitionId,
            1,
            create.Candidate,
            create.SourceKind,
            create.SourceVersion,
            create.CreatedAt,
            create.CreatedAt);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.AgentDefinitionDrafts.Add(AgentDefinitionAdminMapping.MapDraft(draft));
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            throw AgentCoreErrors.Conflict("Draft could not be created.");
        }

        return draft;
    }

    public async ValueTask<AgentDefinitionDraft> UpdateDraftAsync(
        AgentDefinitionDraftUpdate update,
        CancellationToken cancellationToken = default)
    {
        AgentDefinitionValidator.ValidateCandidate(update.Candidate);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.AgentDefinitionDrafts
            .SingleOrDefaultAsync(item => item.DraftId == update.DraftId.ToString("D"), cancellationToken)
            .ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Draft was not found.");

        if (row.Revision != update.ExpectedRevision)
        {
            throw AgentCoreErrors.Conflict("Draft revision is stale.");
        }

        if (!string.Equals(row.DefinitionId, update.Candidate.DefinitionId, StringComparison.Ordinal))
        {
            throw AgentCoreErrors.Validation("definitionId cannot change on update.");
        }

        var current = AgentDefinitionAdminMapping.MapDraft(row);
        var next = current with
        {
            Revision = current.Revision + 1,
            Candidate = update.Candidate,
            UpdatedAt = update.UpdatedAt
        };
        var mapped = AgentDefinitionAdminMapping.MapDraft(next);
        row.Revision = mapped.Revision;
        row.CandidateJson = mapped.CandidateJson;
        row.UpdatedAtUtc = mapped.UpdatedAtUtc;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw AgentCoreErrors.Conflict("Draft revision is stale.");
        }

        return AgentDefinitionAdminMapping.MapDraft(row);
    }

    public async ValueTask<AgentDefinitionDraft> BumpDraftRevisionAsync(
        AgentDefinitionDraftRevisionBump bump,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.AgentDefinitionDrafts
            .SingleOrDefaultAsync(item => item.DraftId == bump.DraftId.ToString("D"), cancellationToken)
            .ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Draft was not found.");

        if (row.Revision != bump.ExpectedRevision)
        {
            throw AgentCoreErrors.Conflict("Draft revision is stale.");
        }

        row.Revision += 1;
        row.UpdatedAtUtc = bump.UpdatedAt.ToUnixTimeMilliseconds();
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw AgentCoreErrors.Conflict("Draft revision is stale.");
        }

        return AgentDefinitionAdminMapping.MapDraft(row);
    }

    public async ValueTask<AgentDefinitionPublication> PublishDraftAsync(
        AgentDefinitionDraftPublish publish,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.AgentDefinitionDrafts
            .SingleOrDefaultAsync(item => item.DraftId == publish.DraftId.ToString("D"), cancellationToken)
            .ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Draft was not found.");

        if (row.Revision != publish.ExpectedRevision)
        {
            throw AgentCoreErrors.Conflict("Draft revision is stale.");
        }

        var draft = AgentDefinitionAdminMapping.MapDraft(row);
        var nextVersion = publish.OccupiedVersions.DefaultIfEmpty(0).Max() + 1;
        if (publish.OccupiedVersions.Contains(nextVersion))
        {
            throw AgentCoreErrors.Conflict("Publication version collides with an existing version.");
        }

        var exists = await db.AgentDefinitionPublications.AsNoTracking()
            .AnyAsync(
                item => item.DefinitionId == draft.DefinitionId && item.Version == nextVersion,
                cancellationToken)
            .ConfigureAwait(false);
        if (exists)
        {
            throw AgentCoreErrors.Conflict("Publication version collides with an existing version.");
        }

        var payload = draft.Candidate.ToPublished(nextVersion);
        AgentDefinitionValidator.Validate(payload);
        var publication = new AgentDefinitionPublication(
            draft.DefinitionId,
            nextVersion,
            payload,
            draft.Revision,
            DefinitionPublicationStatus.Active,
            1,
            publish.PublishedAt);
        await DefinitionResourcePersistence.VerifyDraftResourcesAsync(
            db,
            content,
            publish.DraftId,
            cancellationToken).ConfigureAwait(false);
        db.AgentDefinitionPublications.Add(AgentDefinitionAdminMapping.MapPublication(publication));
        DefinitionResourcePersistence.BindDraftResourcesToPublication(
            db,
            publish.DraftId,
            draft.DefinitionId,
            nextVersion);
        row.Revision = draft.Revision + 1;
        row.UpdatedAtUtc = publish.PublishedAt.ToUnixTimeMilliseconds();
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            throw AgentCoreErrors.Conflict("Publication version collides with an existing version.");
        }

        return publication;
    }

    public async ValueTask<AgentDefinitionPublication?> GetPublicationAsync(
        string definitionId,
        int version,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.AgentDefinitionPublications.AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.DefinitionId == definitionId && item.Version == version,
                cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : AgentDefinitionAdminMapping.MapPublication(row);
    }

    public async ValueTask<IReadOnlyList<AgentDefinitionPublicationSummary>> ListPublicationsAsync(
        string? definitionId = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query = db.AgentDefinitionPublications.AsNoTracking().AsQueryable();
        if (definitionId is not null)
        {
            query = query.Where(item => item.DefinitionId == definitionId);
        }

        var rows = await query
            .OrderBy(item => item.DefinitionId)
            .ThenBy(item => item.Version)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows
            .Select(row =>
            {
                var publication = AgentDefinitionAdminMapping.MapPublication(row);
                return new AgentDefinitionPublicationSummary(
                    publication.DefinitionId,
                    publication.Version,
                    publication.Status,
                    publication.MetadataRevision,
                    publication.PublishedAt);
            })
            .ToArray();
    }

    public async ValueTask<AgentDefinitionPublication> DeprecatePublicationAsync(
        AgentDefinitionPublicationDeprecate deprecate,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.AgentDefinitionPublications
            .SingleOrDefaultAsync(
                item => item.DefinitionId == deprecate.DefinitionId && item.Version == deprecate.Version,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Publication was not found.");

        if (row.MetadataRevision != deprecate.ExpectedMetadataRevision)
        {
            throw AgentCoreErrors.Conflict("Publication metadata revision is stale.");
        }

        if (row.Status == (int)DefinitionPublicationStatus.Deprecated)
        {
            return AgentDefinitionAdminMapping.MapPublication(row);
        }

        row.Status = (int)DefinitionPublicationStatus.Deprecated;
        row.MetadataRevision = deprecate.ExpectedMetadataRevision + 1;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw AgentCoreErrors.Conflict("Publication metadata revision is stale.");
        }

        return AgentDefinitionAdminMapping.MapPublication(row);
    }
}
