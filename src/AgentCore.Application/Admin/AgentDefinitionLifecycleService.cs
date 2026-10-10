using System.Collections.Concurrent;
using System.Diagnostics;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Admin;

public sealed class AgentDefinitionLifecycleService(
    IBuiltInAgentDefinitionStore builtIns,
    IAgentDefinitionAdminStore admin,
    ProviderAliasSet aliases,
    TimeProvider time,
    IIdGenerator ids,
    IAdminLifecycleDeletion? deletion = null,
    AdminLifecycleCoordinator? lifecycleGate = null, IAgentDefinitionResourceAdminStore? resources = null)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _newDefinitionGates = new(StringComparer.Ordinal);
    public ValueTask<IReadOnlyList<AgentDefinitionDraftSummary>> ListDraftsAsync(
        CancellationToken cancellationToken = default) =>
        admin.ListDraftsAsync(cancellationToken);

    public async ValueTask<AgentDefinitionDraft> GetDraftAsync(Guid draftId, CancellationToken cancellationToken = default) =>
        await admin.GetDraftAsync(draftId, cancellationToken).ConfigureAwait(false)
        ?? throw AgentCoreErrors.NotFound("Definition draft was not found.");

    public async ValueTask<AgentDefinitionExactSource> GetEffectiveExactSourceAsync(string definitionId,
        int version, CancellationToken cancellationToken = default) =>
        await AgentDefinitionExactSourceResolver.ResolveAsync(builtIns, admin, definitionId, version,
            cancellationToken).ConfigureAwait(false)
        ?? throw AgentCoreErrors.NotFound("Definition version was not found.");

    public ValueTask<AgentDefinitionCandidate> GetVersionCandidateAsync(
        string definitionId, int version, DefinitionDraftSourceKind sourceKind,
        CancellationToken cancellationToken = default) => sourceKind switch
    {
        DefinitionDraftSourceKind.ForkBuiltIn => ReadBuiltInCandidateAsync(definitionId, version, cancellationToken),
        DefinitionDraftSourceKind.ForkDurable => ReadDurableCandidateAsync(definitionId, version, cancellationToken),
        _ => throw AgentCoreErrors.Validation("Version source must be ForkBuiltIn or ForkDurable.")
    };

    public async ValueTask<AgentDefinition?> GetDraftSourceAsync(AgentDefinitionDraft draft,
        CancellationToken cancellationToken = default) => draft.SourceKind switch
    {
        DefinitionDraftSourceKind.ForkBuiltIn when draft.SourceVersion is { } version =>
            await builtIns.GetAsync(draft.DefinitionId, version, cancellationToken).ConfigureAwait(false),
        DefinitionDraftSourceKind.ForkDurable when draft.SourceVersion is { } version =>
            (await admin.GetPublicationAsync(draft.DefinitionId, version, cancellationToken).ConfigureAwait(false))?.Payload,
        _ => null
    };

    public async ValueTask<AgentDefinitionDraft> CreateDraftAsync(
        string definitionId,
        AgentDefinitionCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        if (!string.Equals(definitionId, candidate.DefinitionId, StringComparison.Ordinal))
        {
            OperationalDiagnostics.RecordAdmin(
                "draftCreate", "rejected", "validation", started, definitionId, null, null, null);
            throw AgentCoreErrors.Validation("definitionId must match the candidate definitionId.");
        }

        candidate = AgentCore.Application.Tools.CapabilityAuthorizationResolver.ResolveCandidate(candidate);
        AgentDefinitionCandidateValidator.ValidateForPersistence(candidate, aliases);
        var draft = await WithDefinitionGateAsync(
            definitionId,
            async innerToken =>
            {
                try
                {
                    await EnsureNewDefinitionIdIsAvailableAsync(definitionId, innerToken).ConfigureAwait(false);
                    var now = time.GetUtcNow();
                    return await admin.CreateDraftAsync(
                        new AgentDefinitionDraftCreate(
                            definitionId,
                            candidate,
                            DefinitionDraftSourceKind.New,
                            null,
                            now,
                            ids.NewId()),
                        innerToken).ConfigureAwait(false);
                }
                catch (AgentCoreException ex) when (ex.Code == "Conflict")
                {
                    OperationalDiagnostics.RecordAdmin(
                        "draftCreate", "rejected", "conflict", started, definitionId, null, null, null);
                    throw;
                }
            },
            cancellationToken).ConfigureAwait(false);
        OperationalDiagnostics.RecordAdmin(
            "draftCreate", "completed", "completed", started, draft.DefinitionId, null, null, "none");
        return draft;
    }

    public ValueTask<AgentDefinitionDraft> CreateNewDraftAsync(
        string definitionId,
        CancellationToken cancellationToken = default) =>
        CreateDraftAsync(definitionId, AgentDefinitionStarter.Create(definitionId, aliases), cancellationToken);

    public ValueTask<AgentDefinitionDraft> ForkDraftAsync(
        string definitionId,
        int sourceVersion,
        DefinitionDraftSourceKind sourceKind,
        CancellationToken cancellationToken = default) =>
        WithDefinitionGateAsync(
            definitionId,
            ct => ForkDraftCoreAsync(definitionId, sourceVersion, sourceKind, ct),
            cancellationToken);

    private async ValueTask<AgentDefinitionDraft> ForkDraftCoreAsync(
        string definitionId,
        int sourceVersion,
        DefinitionDraftSourceKind sourceKind,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        if (sourceKind is not (DefinitionDraftSourceKind.ForkBuiltIn or DefinitionDraftSourceKind.ForkDurable))
        {
            OperationalDiagnostics.RecordAdmin(
                "draftFork", "rejected", "validation", started, definitionId, sourceVersion, null, null);
            throw AgentCoreErrors.Validation("Fork source kind must be forkBuiltIn or forkDurable.");
        }

        var candidate = sourceKind switch
        {
            DefinitionDraftSourceKind.ForkBuiltIn => await ReadBuiltInCandidateAsync(definitionId, sourceVersion, cancellationToken)
                .ConfigureAwait(false),
            _ => await ReadDurableCandidateAsync(definitionId, sourceVersion, cancellationToken)
                .ConfigureAwait(false)
        };

        var now = time.GetUtcNow();
        var draft = await admin.CreateDraftAsync(
            new AgentDefinitionDraftCreate(
                definitionId,
                candidate,
                sourceKind,
                sourceVersion,
                now,
                ids.NewId()),
            cancellationToken).ConfigureAwait(false);
        if (sourceKind == DefinitionDraftSourceKind.ForkDurable && resources is not null)
        {
            var inherited = await resources.ListPublicationResourcesAsync(definitionId, sourceVersion, cancellationToken);
            if (inherited.Count > 0)
            {
                await resources.BindDraftResourcesAsync(new(draft.DraftId, draft.Revision, inherited.Select(r => new AgentDefinitionDraftResourceBatchItem(r.ResourceId, r.LogicalPath, r.Kind, r.MediaType, r.ContentSha256, r.ByteLength)).ToArray(), now), cancellationToken);
                draft = await GetDraftAsync(draft.DraftId, cancellationToken);
            }
        }
        OperationalDiagnostics.RecordAdmin(
            "draftFork", "completed", "completed", started, draft.DefinitionId, sourceVersion, null, "none");
        return draft;
    }

    public ValueTask<AgentDefinitionDraft> UpdateDraftAsync(
        Guid draftId,
        long expectedRevision,
        AgentDefinitionCandidate candidate,
        CancellationToken cancellationToken = default,
        AdminEventAppend? history = null) =>
        WithDraftDefinitionGateAsync(
            draftId,
            async (draft, innerToken) =>
            {
                var started = Stopwatch.GetTimestamp();
                if (draft.Revision != expectedRevision)
                {
                    OperationalDiagnostics.RecordAdmin(
                        "draftUpdate", "rejected", "conflict", started, draft.DefinitionId, null, null, null);
                    throw AgentCoreErrors.Conflict("Draft revision is stale.");
                }

                if (!string.Equals(draft.DefinitionId, candidate.DefinitionId, StringComparison.Ordinal))
                {
                    OperationalDiagnostics.RecordAdmin(
                        "draftUpdate", "rejected", "validation", started, draft.DefinitionId, null, null, null);
                    throw AgentCoreErrors.Validation("definitionId cannot change on update.");
                }

                candidate = AgentCore.Application.Tools.CapabilityAuthorizationResolver.ResolveCandidate(candidate);
                AgentDefinitionCandidateValidator.ValidateForPersistence(candidate, aliases);
                var updated = await admin.UpdateDraftAsync(
                    new AgentDefinitionDraftUpdate(draftId, expectedRevision, candidate, time.GetUtcNow(), history),
                    innerToken).ConfigureAwait(false);
                OperationalDiagnostics.RecordAdmin(
                    "draftUpdate", "completed", "completed", started, updated.DefinitionId, null, null, "none");
                return updated;
            },
            cancellationToken);

    public async ValueTask DeleteDraftAsync(
        Guid draftId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        _ = await WithDraftDefinitionGateAsync(
            draftId,
            async (draft, innerToken) =>
            {
                var started = Stopwatch.GetTimestamp();
                if (draft.Revision != expectedRevision)
                {
                    OperationalDiagnostics.RecordAdmin(
                        "draftDelete", "rejected", "conflict", started, draft.DefinitionId, null, null, null);
                    throw AgentCoreErrors.Conflict("Draft revision is stale.");
                }

                await admin.DeleteDraftAsync(
                    new AgentDefinitionDraftDelete(
                        draftId,
                        expectedRevision,
                        time.GetUtcNow(),
                        ids.NewId()),
                    innerToken).ConfigureAwait(false);
                OperationalDiagnostics.RecordAdmin(
                    "draftDelete", "completed", "completed", started, draft.DefinitionId, null, null, "none");
                return 0;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DeleteLogicalDefinitionAsync(
        AdminDefinitionDeleteCommand command,
        CancellationToken cancellationToken = default) =>
        WithDefinitionGateAsync(
            command.DefinitionId,
            ct => DeleteLogicalDefinitionCoreAsync(command, ct),
            cancellationToken);

    private async ValueTask DeleteLogicalDefinitionCoreAsync(
        AdminDefinitionDeleteCommand command,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        if (deletion is null)
        {
            throw AgentCoreErrors.Validation("Definition deletion is not available.");
        }

        var builtIn = await builtIns.ListAsync(cancellationToken).ConfigureAwait(false);
        if (builtIn.Any(item => string.Equals(item.Id, command.DefinitionId, StringComparison.Ordinal)))
        {
            OperationalDiagnostics.RecordAdmin(
                "definitionDelete", "rejected", "validation", started, command.DefinitionId, null, null, null);
            throw AgentCoreErrors.Validation("Built-in definitions cannot be deleted.");
        }

        try
        {
            await deletion.DeleteDefinitionAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (AgentCoreException ex) when (ex.Code is "Conflict" or "NotFound" or "ValidationError")
        {
            OperationalDiagnostics.RecordAdmin(
                "definitionDelete",
                "rejected",
                ex.Code == "NotFound" ? "notFound" : ex.Code == "Conflict" ? "conflict" : "validation",
                started,
                command.DefinitionId,
                null,
                null,
                null);
            throw;
        }

        OperationalDiagnostics.RecordAdmin(
            "definitionDelete", "completed", "completed", started, command.DefinitionId, null, null, "deleted");
    }

    internal ValueTask<T> WithDraftDefinitionGateAsync<T>(
        Guid draftId,
        Func<AgentDefinitionDraft, CancellationToken, ValueTask<T>> action,
        CancellationToken cancellationToken = default)
    {
        var preview = admin.GetDraftAsync(draftId, cancellationToken);
        return WithDraftDefinitionGateCoreAsync(preview, draftId, action, cancellationToken);
    }

    private async ValueTask<T> WithDraftDefinitionGateCoreAsync<T>(
        ValueTask<AgentDefinitionDraft?> previewTask,
        Guid draftId,
        Func<AgentDefinitionDraft, CancellationToken, ValueTask<T>> action,
        CancellationToken cancellationToken)
    {
        var preview = await previewTask.ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Definition draft was not found.");
        return await WithDefinitionGateAsync(
            preview.DefinitionId,
            async innerToken =>
            {
                var draft = await admin.GetDraftAsync(draftId, innerToken).ConfigureAwait(false)
                    ?? throw AgentCoreErrors.NotFound("Definition draft was not found.");
                return await action(draft, innerToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<T> WithDefinitionGateAsync<T>(
        string definitionId,
        Func<CancellationToken, ValueTask<T>> action,
        CancellationToken cancellationToken)
    {
        if (lifecycleGate is not null)
        {
            return await lifecycleGate.WithDefinitionAsync(definitionId, action, cancellationToken)
                .ConfigureAwait(false);
        }

        var gate = _newDefinitionGates.GetOrAdd(definitionId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async ValueTask WithDefinitionGateAsync(
        string definitionId,
        Func<CancellationToken, ValueTask> action,
        CancellationToken cancellationToken)
    {
        _ = await WithDefinitionGateAsync(
            definitionId,
            async ct =>
            {
                await action(ct).ConfigureAwait(false);
                return 0;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask EnsureNewDefinitionIdIsAvailableAsync(
        string definitionId,
        CancellationToken cancellationToken)
    {
        var builtIn = await builtIns.ListAsync(cancellationToken).ConfigureAwait(false);
        if (builtIn.Any(item => string.Equals(item.Id, definitionId, StringComparison.Ordinal)))
        {
            throw AgentCoreErrors.Conflict(AdminDeletionMessages.DefinitionAlreadyExists(definitionId));
        }

        var publications = await admin.ListPublicationsAsync(definitionId, cancellationToken).ConfigureAwait(false);
        if (publications.Count > 0)
        {
            throw AgentCoreErrors.Conflict(AdminDeletionMessages.DefinitionAlreadyExists(definitionId));
        }

        var drafts = await admin.ListDraftsAsync(cancellationToken).ConfigureAwait(false);
        if (drafts.Any(item => string.Equals(item.DefinitionId, definitionId, StringComparison.Ordinal)))
        {
            throw AgentCoreErrors.Conflict(AdminDeletionMessages.DefinitionAlreadyExists(definitionId));
        }
    }

    internal async ValueTask<AgentDefinitionPublication> CommitDraftPublicationAsync(
        Guid draftId,
        long expectedRevision,
        Guid operationId,
        IReadOnlyList<string> changedSectionIds,
        CancellationToken cancellationToken = default,
        AdminEventActorKind actorKind = AdminEventActorKind.LocalOwner,
        bool consumeDraft = false)
    {
        var draft = await GetDraftAsync(draftId, cancellationToken).ConfigureAwait(false);
        return await WithDefinitionGateAsync(
            draft.DefinitionId,
            ct => CommitDraftPublicationCoreAsync(
                draftId,
                expectedRevision,
                operationId,
                changedSectionIds,
                ct, actorKind, consumeDraft),
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<AgentDefinitionPublication> CommitDraftPublicationCoreAsync(
        Guid draftId,
        long expectedRevision,
        Guid operationId,
        IReadOnlyList<string> changedSectionIds,
        CancellationToken cancellationToken = default,
        AdminEventActorKind actorKind = AdminEventActorKind.LocalOwner,
        bool consumeDraft = false)
    {
        var started = Stopwatch.GetTimestamp();
        var draft = await GetDraftAsync(draftId, cancellationToken).ConfigureAwait(false);
        if (draft.Revision != expectedRevision)
        {
            OperationalDiagnostics.RecordAdmin(
                "publish", "rejected", "conflict", started, draft.DefinitionId, null, null, null);
            throw AgentCoreErrors.Conflict("Draft revision is stale.");
        }

        var occupied = await GetOccupiedVersionsAsync(draft.DefinitionId, cancellationToken).ConfigureAwait(false);
        var publication = await admin.PublishDraftAsync(
            new AgentDefinitionDraftPublish(
                draftId,
                expectedRevision,
                occupied,
                time.GetUtcNow(),
                operationId,
                ActorKind: actorKind, ChangedSectionIds: changedSectionIds, ConsumeDraft: consumeDraft),
            cancellationToken).ConfigureAwait(false);
        OperationalDiagnostics.RecordAdmin(
            "publish",
            "completed",
            "completed",
            started,
            publication.DefinitionId,
            publication.Version,
            null,
            "published");
        return publication;
    }

    public ValueTask<IReadOnlyList<AgentDefinitionPublicationSummary>> ListPublicationsAsync(
        string definitionId,
        CancellationToken cancellationToken = default) =>
        admin.ListPublicationsAsync(definitionId, cancellationToken);

    public ValueTask<AgentDefinitionPublication> DeprecatePublicationAsync(
        string definitionId,
        int version,
        long expectedMetadataRevision,
        CancellationToken cancellationToken = default) =>
        WithDefinitionGateAsync(
            definitionId,
            ct => DeprecatePublicationCoreAsync(definitionId, version, expectedMetadataRevision, ct),
            cancellationToken);

    private async ValueTask<AgentDefinitionPublication> DeprecatePublicationCoreAsync(
        string definitionId,
        int version,
        long expectedMetadataRevision,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        var durable = await admin.GetPublicationAsync(definitionId, version, cancellationToken).ConfigureAwait(false);
        if (durable is null)
        {
            var builtIn = await builtIns.GetAsync(definitionId, version, cancellationToken).ConfigureAwait(false);
            if (builtIn is not null)
            {
                OperationalDiagnostics.RecordAdmin(
                    "deprecate", "rejected", "validation", started, definitionId, version, null, null);
                throw AgentCoreErrors.Validation("Built-in definition versions cannot be deprecated.");
            }

            OperationalDiagnostics.RecordAdmin(
                "deprecate", "rejected", "notFound", started, definitionId, version, null, null);
            throw AgentCoreErrors.NotFound("Publication was not found.");
        }

        if (durable.MetadataRevision != expectedMetadataRevision)
        {
            OperationalDiagnostics.RecordAdmin(
                "deprecate", "rejected", "conflict", started, definitionId, version, null, null);
            throw AgentCoreErrors.Conflict("Publication metadata revision is stale.");
        }

        var publication = await admin.DeprecatePublicationAsync(
            new AgentDefinitionPublicationDeprecate(
                definitionId,
                version,
                expectedMetadataRevision,
                time.GetUtcNow(),
                ids.NewId()),
            cancellationToken).ConfigureAwait(false);
        OperationalDiagnostics.RecordAdmin(
            "deprecate",
            "completed",
            "completed",
            started,
            publication.DefinitionId,
            publication.Version,
            null,
            "deprecated");
        return publication;
    }

    private async ValueTask<AgentDefinitionCandidate> ReadBuiltInCandidateAsync(
        string definitionId,
        int sourceVersion,
        CancellationToken cancellationToken)
    {
        var definition = await builtIns.GetAsync(definitionId, sourceVersion, cancellationToken).ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Built-in definition version was not found.");
        return AgentDefinitionCandidate.FromDefinition(definition);
    }

    private async ValueTask<AgentDefinitionCandidate> ReadDurableCandidateAsync(
        string definitionId,
        int sourceVersion,
        CancellationToken cancellationToken)
    {
        var publication = await admin.GetPublicationAsync(definitionId, sourceVersion, cancellationToken)
            .ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Durable publication was not found.");
        return AgentDefinitionCandidate.FromDefinition(publication.Payload);
    }

    private async ValueTask<IReadOnlyCollection<int>> GetOccupiedVersionsAsync(
        string definitionId,
        CancellationToken cancellationToken)
    {
        var versions = new HashSet<int>();
        foreach (var definition in await builtIns.ListAsync(cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(definition.Id, definitionId, StringComparison.Ordinal))
            {
                versions.Add(definition.Version);
            }
        }

        foreach (var summary in await admin.ListPublicationsAsync(definitionId, cancellationToken).ConfigureAwait(false))
        {
            versions.Add(summary.Version);
        }

        return versions;
    }
}
