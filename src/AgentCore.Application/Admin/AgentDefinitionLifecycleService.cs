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
    IIdGenerator ids)
{
    public ValueTask<IReadOnlyList<AgentDefinitionDraftSummary>> ListDraftsAsync(
        CancellationToken cancellationToken = default) =>
        admin.ListDraftsAsync(cancellationToken);

    public async ValueTask<AgentDefinitionDraft> GetDraftAsync(Guid draftId, CancellationToken cancellationToken = default) =>
        await admin.GetDraftAsync(draftId, cancellationToken).ConfigureAwait(false)
        ?? throw AgentCoreErrors.NotFound("Definition draft was not found.");

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

        AgentDefinitionCandidateValidator.ValidateForPersistence(candidate, aliases);
        var now = time.GetUtcNow();
        var draft = await admin.CreateDraftAsync(
            new AgentDefinitionDraftCreate(
                definitionId,
                candidate,
                DefinitionDraftSourceKind.New,
                null,
                now,
                ids.NewId()),
            cancellationToken).ConfigureAwait(false);
        OperationalDiagnostics.RecordAdmin(
            "draftCreate", "completed", "completed", started, draft.DefinitionId, null, null, "none");
        return draft;
    }

    public ValueTask<AgentDefinitionDraft> CreateNewDraftAsync(
        string definitionId,
        CancellationToken cancellationToken = default) =>
        CreateDraftAsync(definitionId, AgentDefinitionStarter.Create(definitionId, aliases), cancellationToken);

    public async ValueTask<AgentDefinitionDraft> ForkDraftAsync(
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
            DefinitionDraftSourceKind.ForkBuiltIn => await ForkFromBuiltInAsync(definitionId, sourceVersion, cancellationToken)
                .ConfigureAwait(false),
            _ => await ForkFromDurableAsync(definitionId, sourceVersion, cancellationToken)
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
        OperationalDiagnostics.RecordAdmin(
            "draftFork", "completed", "completed", started, draft.DefinitionId, sourceVersion, null, "none");
        return draft;
    }

    public async ValueTask<AgentDefinitionDraft> UpdateDraftAsync(
        Guid draftId,
        long expectedRevision,
        AgentDefinitionCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        var existing = await GetDraftAsync(draftId, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(existing.DefinitionId, candidate.DefinitionId, StringComparison.Ordinal))
        {
            OperationalDiagnostics.RecordAdmin(
                "draftUpdate", "rejected", "validation", started, existing.DefinitionId, null, null, null);
            throw AgentCoreErrors.Validation("definitionId cannot change on update.");
        }

        AgentDefinitionCandidateValidator.ValidateForPersistence(candidate, aliases);
        var draft = await admin.UpdateDraftAsync(
            new AgentDefinitionDraftUpdate(draftId, expectedRevision, candidate, time.GetUtcNow()),
            cancellationToken).ConfigureAwait(false);
        OperationalDiagnostics.RecordAdmin(
            "draftUpdate", "completed", "completed", started, draft.DefinitionId, null, null, "none");
        return draft;
    }

    public async ValueTask DeleteDraftAsync(
        Guid draftId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        await admin.DeleteDraftAsync(
            new AgentDefinitionDraftDelete(
                draftId,
                expectedRevision,
                time.GetUtcNow(),
                ids.NewId()),
            cancellationToken).ConfigureAwait(false);
        OperationalDiagnostics.RecordAdmin(
            "draftDelete", "completed", "completed", started, null, null, null, "none");
    }

    internal async ValueTask<AgentDefinitionPublication> CommitDraftPublicationAsync(
        Guid draftId,
        long expectedRevision,
        Guid operationId,
        IReadOnlyList<string> changedSectionIds,
        CancellationToken cancellationToken = default)
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
                ChangedSectionIds: changedSectionIds),
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

    public async ValueTask<AgentDefinitionPublication> DeprecatePublicationAsync(
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

    private async ValueTask<AgentDefinitionCandidate> ForkFromBuiltInAsync(
        string definitionId,
        int sourceVersion,
        CancellationToken cancellationToken)
    {
        var definition = await builtIns.GetAsync(definitionId, sourceVersion, cancellationToken).ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Built-in definition version was not found.");
        return AgentDefinitionCandidate.FromDefinition(definition);
    }

    private async ValueTask<AgentDefinitionCandidate> ForkFromDurableAsync(
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
