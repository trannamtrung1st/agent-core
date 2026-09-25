using System.Collections.Concurrent;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;

namespace AgentCore.Infrastructure.Definitions;

public sealed class InMemoryAgentDefinitionAdminStore(IIdGenerator ids) : IAgentDefinitionAdminStore
{
    private readonly ConcurrentDictionary<Guid, AgentDefinitionDraft> _drafts = new();
    private readonly ConcurrentDictionary<(string DefinitionId, int Version), AgentDefinitionPublication> _publications = new();

    public ValueTask<IReadOnlyList<AgentDefinitionDraftSummary>> ListDraftsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var items = _drafts.Values
            .OrderBy(item => item.DefinitionId, StringComparer.Ordinal)
            .ThenByDescending(item => item.UpdatedAt)
            .Select(item => new AgentDefinitionDraftSummary(
                item.DraftId,
                item.DefinitionId,
                item.Revision,
                item.SourceKind,
                item.SourceVersion,
                item.UpdatedAt))
            .ToArray();
        return ValueTask.FromResult<IReadOnlyList<AgentDefinitionDraftSummary>>(items);
    }

    public ValueTask<AgentDefinitionDraft?> GetDraftAsync(Guid draftId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_drafts.TryGetValue(draftId, out var draft) ? draft : null);
    }

    public ValueTask<AgentDefinitionDraft> CreateDraftAsync(
        AgentDefinitionDraftCreate create,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
        if (!_drafts.TryAdd(draft.DraftId, draft))
        {
            throw AgentCoreErrors.Conflict("Draft could not be created.");
        }

        return ValueTask.FromResult(draft);
    }

    public ValueTask<AgentDefinitionDraft> UpdateDraftAsync(
        AgentDefinitionDraftUpdate update,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AgentDefinitionValidator.ValidateCandidate(update.Candidate);
        if (!_drafts.TryGetValue(update.DraftId, out var current))
        {
            throw AgentCoreErrors.NotFound("Draft was not found.");
        }

        if (current.Revision != update.ExpectedRevision)
        {
            throw AgentCoreErrors.Conflict("Draft revision is stale.");
        }

        if (!string.Equals(current.DefinitionId, update.Candidate.DefinitionId, StringComparison.Ordinal))
        {
            throw AgentCoreErrors.Validation("definitionId cannot change on update.");
        }

        var next = current with
        {
            Revision = current.Revision + 1,
            Candidate = update.Candidate,
            UpdatedAt = update.UpdatedAt
        };
        if (!_drafts.TryUpdate(update.DraftId, next, current))
        {
            throw AgentCoreErrors.Conflict("Draft revision is stale.");
        }

        return ValueTask.FromResult(next);
    }

    public ValueTask<AgentDefinitionPublication> PublishDraftAsync(
        AgentDefinitionDraftPublish publish,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_drafts.TryGetValue(publish.DraftId, out var draft))
        {
            throw AgentCoreErrors.NotFound("Draft was not found.");
        }

        if (draft.Revision != publish.ExpectedRevision)
        {
            throw AgentCoreErrors.Conflict("Draft revision is stale.");
        }

        var nextVersion = publish.OccupiedVersions.DefaultIfEmpty(0).Max() + 1;
        if (publish.OccupiedVersions.Contains(nextVersion)
            || _publications.ContainsKey((draft.DefinitionId, nextVersion)))
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
        if (!_publications.TryAdd((draft.DefinitionId, nextVersion), publication))
        {
            throw AgentCoreErrors.Conflict("Publication version collides with an existing version.");
        }

        var nextDraft = draft with
        {
            Revision = draft.Revision + 1,
            UpdatedAt = publish.PublishedAt
        };
        _drafts.TryUpdate(publish.DraftId, nextDraft, draft);

        return ValueTask.FromResult(publication);
    }

    public ValueTask<AgentDefinitionPublication?> GetPublicationAsync(
        string definitionId,
        int version,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            _publications.TryGetValue((definitionId, version), out var publication) ? publication : null);
    }

    public ValueTask<IReadOnlyList<AgentDefinitionPublicationSummary>> ListPublicationsAsync(
        string? definitionId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var query = _publications.Values.AsEnumerable();
        if (definitionId is not null)
        {
            query = query.Where(item => string.Equals(item.DefinitionId, definitionId, StringComparison.Ordinal));
        }

        var items = query
            .OrderBy(item => item.DefinitionId, StringComparer.Ordinal)
            .ThenBy(item => item.Version)
            .Select(item => new AgentDefinitionPublicationSummary(
                item.DefinitionId,
                item.Version,
                item.Status,
                item.PublishedAt))
            .ToArray();
        return ValueTask.FromResult<IReadOnlyList<AgentDefinitionPublicationSummary>>(items);
    }

    public ValueTask<AgentDefinitionPublication> DeprecatePublicationAsync(
        AgentDefinitionPublicationDeprecate deprecate,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_publications.TryGetValue((deprecate.DefinitionId, deprecate.Version), out var current))
        {
            throw AgentCoreErrors.NotFound("Publication was not found.");
        }

        if (current.MetadataRevision != deprecate.ExpectedMetadataRevision)
        {
            throw AgentCoreErrors.Conflict("Publication metadata revision is stale.");
        }

        if (current.Status == DefinitionPublicationStatus.Deprecated)
        {
            return ValueTask.FromResult(current);
        }

        var next = current with
        {
            Status = DefinitionPublicationStatus.Deprecated,
            MetadataRevision = current.MetadataRevision + 1
        };
        if (!_publications.TryUpdate((deprecate.DefinitionId, deprecate.Version), next, current))
        {
            throw AgentCoreErrors.Conflict("Publication metadata revision is stale.");
        }

        return ValueTask.FromResult(next);
    }
}
