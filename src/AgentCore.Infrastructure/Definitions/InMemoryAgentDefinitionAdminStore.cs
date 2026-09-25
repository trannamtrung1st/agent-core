using System.Collections.Concurrent;
using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Admin;
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
        if (!_drafts.TryGetValue(draftId, out var draft))
        {
            return ValueTask.FromResult<AgentDefinitionDraft?>(null);
        }

        return ValueTask.FromResult<AgentDefinitionDraft?>(AgentDefinitionAdminSnapshots.Freeze(draft));
    }

    public ValueTask<AgentDefinitionDraft> CreateDraftAsync(
        AgentDefinitionDraftCreate create,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var candidate = AgentDefinitionAdminSnapshots.Freeze(create.Candidate);
        AgentDefinitionValidator.ValidateCandidate(candidate);
        if (!string.Equals(create.DefinitionId, candidate.DefinitionId, StringComparison.Ordinal))
        {
            throw AgentCoreErrors.Validation("definitionId must match the candidate definitionId.");
        }

        if (create.OperationId != Guid.Empty)
        {
            if (EventStore is null)
            {
                throw AgentCoreErrors.Validation("Admin draft history is not available.");
            }

            var operationGate = AdminOperationLockRegistry.For(create.OperationId);
            lock (operationGate)
            {
                var existingDraft = TryResolveDraftCreatedByOperationId(create.OperationId);
                if (existingDraft is not null)
                {
                    return ValueTask.FromResult(existingDraft);
                }

                return ValueTask.FromResult(CreateDraftWithHistory(create, candidate));
            }
        }

        return ValueTask.FromResult(CreateDraftWithHistory(create, candidate));
    }

    private AgentDefinitionDraft CreateDraftWithHistory(
        AgentDefinitionDraftCreate create,
        AgentDefinitionCandidate candidate)
    {
        var draftId = ids.NewId();
        AdminEventAppend? historyAppend = null;
        if (create.OperationId != Guid.Empty)
        {
            historyAppend = AdminEventFactory.DraftCreated(
                create.OperationId,
                create.CreatedAt,
                create.DefinitionId,
                draftId,
                create.SourceKind,
                create.SourceVersion,
                create.ActorKind);
        }

        var draft = AgentDefinitionAdminSnapshots.Freeze(new AgentDefinitionDraft(
            draftId,
            create.DefinitionId,
            1,
            candidate,
            create.SourceKind,
            create.SourceVersion,
            create.CreatedAt,
            create.CreatedAt));
        var gate = DefinitionDraftLockRegistry.For(draft.DraftId);
        lock (gate)
        {
            if (!_drafts.TryAdd(draft.DraftId, draft))
            {
                throw AgentCoreErrors.Conflict("Draft could not be created.");
            }

            if (historyAppend is not null)
            {
                try
                {
                    EventStore!.AppendWithinLock(historyAppend);
                }
                catch
                {
                    _drafts.TryRemove(draft.DraftId, out _);
                    throw;
                }
            }
        }

        return AgentDefinitionAdminSnapshots.Freeze(draft);
    }

    private AgentDefinitionDraft? TryResolveDraftCreatedByOperationId(Guid operationId)
    {
        var existingEvent = EventStore!.TryGetByOperationIdAsync(operationId).AsTask().GetAwaiter().GetResult();
        if (existingEvent is null)
        {
            return null;
        }

        if (existingEvent.Operation != AdminEventOperationKind.DraftCreated)
        {
            throw AgentCoreErrors.Conflict("Operation id is already used for a different admin event.");
        }

        if (!Guid.TryParse(existingEvent.TargetId, out var draftId))
        {
            throw AgentCoreErrors.Conflict("Draft created history is missing a draft target id.");
        }

        if (!_drafts.TryGetValue(draftId, out var draft))
        {
            throw AgentCoreErrors.Conflict("Draft created history references a missing draft.");
        }

        return AgentDefinitionAdminSnapshots.Freeze(draft);
    }

    public ValueTask<AgentDefinitionDraft> BumpDraftRevisionAsync(
        AgentDefinitionDraftRevisionBump bump,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var gate = DefinitionDraftLockRegistry.For(bump.DraftId);
        lock (gate)
        {
            if (!_drafts.TryGetValue(bump.DraftId, out var current))
            {
                throw AgentCoreErrors.NotFound("Draft was not found.");
            }

            if (current.Revision != bump.ExpectedRevision)
            {
                throw AgentCoreErrors.Conflict("Draft revision is stale.");
            }

            var next = AgentDefinitionAdminSnapshots.Freeze(current with
            {
                Revision = current.Revision + 1,
                UpdatedAt = bump.UpdatedAt
            });
            if (!_drafts.TryUpdate(bump.DraftId, next, current))
            {
                throw AgentCoreErrors.Conflict("Draft revision is stale.");
            }

            return ValueTask.FromResult(AgentDefinitionAdminSnapshots.Freeze(next));
        }
    }

    public ValueTask<AgentDefinitionDraft> UpdateDraftAsync(
        AgentDefinitionDraftUpdate update,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var candidate = AgentDefinitionAdminSnapshots.Freeze(update.Candidate);
        AgentDefinitionValidator.ValidateCandidate(candidate);
        var gate = DefinitionDraftLockRegistry.For(update.DraftId);
        lock (gate)
        {
            if (!_drafts.TryGetValue(update.DraftId, out var current))
            {
                throw AgentCoreErrors.NotFound("Draft was not found.");
            }

            if (current.Revision != update.ExpectedRevision)
            {
                throw AgentCoreErrors.Conflict("Draft revision is stale.");
            }

            if (!string.Equals(current.DefinitionId, candidate.DefinitionId, StringComparison.Ordinal))
            {
                throw AgentCoreErrors.Validation("definitionId cannot change on update.");
            }

            var next = AgentDefinitionAdminSnapshots.Freeze(current with
            {
                Revision = current.Revision + 1,
                Candidate = candidate,
                UpdatedAt = update.UpdatedAt
            });
            if (!_drafts.TryUpdate(update.DraftId, next, current))
            {
                throw AgentCoreErrors.Conflict("Draft revision is stale.");
            }

            return ValueTask.FromResult(AgentDefinitionAdminSnapshots.Freeze(next));
        }
    }

    public ValueTask<AgentDefinitionPublication> PublishDraftAsync(
        AgentDefinitionDraftPublish publish,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var gate = DefinitionDraftLockRegistry.For(publish.DraftId);
        lock (gate)
        {
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

            var payload = AgentDefinitionAdminSnapshots.Freeze(draft.Candidate.ToPublished(nextVersion));
            AgentDefinitionValidator.Validate(payload);
            var publication = AgentDefinitionAdminSnapshots.Freeze(new AgentDefinitionPublication(
                draft.DefinitionId,
                nextVersion,
                payload,
                draft.Revision,
                DefinitionPublicationStatus.Active,
                1,
                publish.PublishedAt));

            AdminEventAppend? historyAppend = null;
            if (publish.OperationId != Guid.Empty)
            {
                if (EventStore is null)
                {
                    throw AgentCoreErrors.Validation("Admin publication history is not available.");
                }

                historyAppend = AdminEventFactory.PublicationCreated(
                    publish.OperationId,
                    publish.PublishedAt,
                    draft.DefinitionId,
                    nextVersion,
                    publish.DraftId,
                    draft.Revision,
                    publish.ChangedSectionIds ?? [],
                    publish.ActorKind);
            }

            var nextDraft = draft with
            {
                Revision = draft.Revision + 1,
                UpdatedAt = publish.PublishedAt
            };
            if (!_drafts.TryUpdate(publish.DraftId, nextDraft, draft))
            {
                throw AgentCoreErrors.Conflict("Draft revision is stale.");
            }

            if (!_publications.TryAdd((draft.DefinitionId, nextVersion), publication))
            {
                _drafts.TryUpdate(publish.DraftId, draft, nextDraft);
                throw AgentCoreErrors.Conflict("Publication version collides with an existing version.");
            }

            ResourceStore?.SnapshotPublicationOnPublish(publish.DraftId, draft.DefinitionId, nextVersion);

            if (historyAppend is not null)
            {
                try
                {
                    EventStore!.AppendWithinLock(historyAppend);
                }
                catch
                {
                    _publications.TryRemove((draft.DefinitionId, nextVersion), out _);
                    _drafts.TryUpdate(publish.DraftId, draft, nextDraft);
                    ResourceStore?.RevertPublicationResourceSnapshot(draft.DefinitionId, nextVersion);
                    throw;
                }
            }

            return ValueTask.FromResult(AgentDefinitionAdminSnapshots.Freeze(publication));
        }
    }

    public ValueTask<AgentDefinitionPublication?> GetPublicationAsync(
        string definitionId,
        int version,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_publications.TryGetValue((definitionId, version), out var publication))
        {
            return ValueTask.FromResult<AgentDefinitionPublication?>(null);
        }

        return ValueTask.FromResult<AgentDefinitionPublication?>(AgentDefinitionAdminSnapshots.Freeze(publication));
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
                item.MetadataRevision,
                item.PublishedAt))
            .ToArray();
        return ValueTask.FromResult<IReadOnlyList<AgentDefinitionPublicationSummary>>(items);
    }

    public ValueTask<AgentDefinitionPublication> DeprecatePublicationAsync(
        AgentDefinitionPublicationDeprecate deprecate,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var gate = DefinitionPublicationLockRegistry.For(deprecate.DefinitionId, deprecate.Version);
        lock (gate)
        {
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
                return ValueTask.FromResult(AgentDefinitionAdminSnapshots.Freeze(current));
            }

            var nextMetadataRevision = current.MetadataRevision + 1;
            AdminEventAppend? historyAppend = null;
            if (deprecate.OperationId != Guid.Empty)
            {
                if (EventStore is null)
                {
                    throw AgentCoreErrors.Validation("Admin publication history is not available.");
                }

                historyAppend = AdminEventFactory.PublicationDeprecated(
                    deprecate.OperationId,
                    deprecate.UpdatedAt,
                    deprecate.DefinitionId,
                    deprecate.Version,
                    nextMetadataRevision,
                    deprecate.ActorKind);
            }

            var next = AgentDefinitionAdminSnapshots.Freeze(current with
            {
                Status = DefinitionPublicationStatus.Deprecated,
                MetadataRevision = nextMetadataRevision
            });
            if (!_publications.TryUpdate((deprecate.DefinitionId, deprecate.Version), next, current))
            {
                throw AgentCoreErrors.Conflict("Publication metadata revision is stale.");
            }

            if (historyAppend is not null)
            {
                try
                {
                    EventStore!.AppendWithinLock(historyAppend);
                }
                catch
                {
                    _publications.TryUpdate((deprecate.DefinitionId, deprecate.Version), current, next);
                    throw;
                }
            }

            return ValueTask.FromResult(AgentDefinitionAdminSnapshots.Freeze(next));
        }
    }

    internal InMemoryAgentDefinitionResourceAdminStore? ResourceStore { get; set; }

    internal InMemoryAdminEventStore? EventStore { get; set; }
}
