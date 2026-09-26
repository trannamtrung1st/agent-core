using AgentCore.Application.Models;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Triggers;
using AgentCore.Domain.Work;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Work;

public sealed record DurableIntakePass(int Accepted, int Existing, int Skipped);

public sealed class DurableWorkIntake(
    ITriggerStore triggers,
    IDurableWorkHandoff handoff,
    IAgentInstanceStore instances,
    IAgentDefinitionStore definitions,
    IModelCatalog catalog,
    IIdGenerator ids,
    TimeProvider time,
    ILogger<DurableWorkIntake> logger)
{
    public async ValueTask<DurableIntakePass> AcceptAwaitingAsync(CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        var awaiting = await triggers.ListByDispositionAsync(
            OccurrenceRoutingDisposition.AwaitingDurableWork,
            TriggerScheduler.DefaultBatchSize,
            cancellationToken).ConfigureAwait(false);
        var accepted = 0;
        var existing = 0;
        var skipped = 0;
        foreach (var occurrence in awaiting)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var proposed = await ProposeAsync(occurrence, now, cancellationToken).ConfigureAwait(false);
                if (proposed is null)
                {
                    skipped++;
                    RuntimeTelemetry.RecordWork("skipped");
                    continue;
                }

                var result = await handoff.AcceptAsync(occurrence.OccurrenceId, proposed, now, cancellationToken)
                    .ConfigureAwait(false);
                if (result.Kind == WorkItemCreateKind.Created)
                {
                    accepted++;
                    RuntimeTelemetry.RecordWork("accepted");
                }
                else
                {
                    existing++;
                    RuntimeTelemetry.RecordWork("existing");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                skipped++;
                RuntimeTelemetry.RecordWork("skipped");
                logger.LogWarning(
                    "Durable intake skipped an occurrence ({ExceptionType}).",
                    exception.GetType().Name);
            }
        }

        return new DurableIntakePass(accepted, existing, skipped);
    }

    private async ValueTask<WorkItem?> ProposeAsync(
        TriggerOccurrence occurrence,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var sourceKind = occurrence.SourceKind switch
        {
            TriggerSourceKind.Schedule => WorkSourceKind.Schedule,
            TriggerSourceKind.ApplicationEvent => WorkSourceKind.ApplicationEvent,
            _ => (WorkSourceKind?)null
        };
        if (sourceKind is not WorkSourceKind kind)
        {
            return null;
        }

        var instance = await instances.FindAsync(occurrence.Owner.AgentInstanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null || instance.Lifecycle != AgentCore.Domain.Definitions.AgentInstanceLifecycle.Active)
        {
            return null;
        }

        var definition = await definitions.GetAsync(instance.DefinitionId, instance.ActiveVersion, cancellationToken)
            .ConfigureAwait(false);
        if (definition is null)
        {
            return null;
        }

        Guid? sourceSessionId = null;
        if (occurrence.RegistrationId is Guid registrationId)
        {
            var registration = await triggers.GetAsync(
                occurrence.Owner,
                registrationId,
                cancellationToken).ConfigureAwait(false);
            sourceSessionId = registration?.Provenance.SourceSessionId;
        }

        var selection = SessionModelBinder.PinDefault(catalog, definition);
        return WorkItem.Create(
            ids.NewId(),
            new WorkOwner(occurrence.Owner.AgentInstanceId, occurrence.Owner.ProfileId),
            new WorkProvenance(
                occurrence.OccurrenceId,
                kind,
                occurrence.RegistrationId,
                sourceSessionId,
                occurrence.SourceEventId,
                occurrence.DedupeKey,
                occurrence.ScheduledAtUtc,
                occurrence.ObservedAtUtc,
                occurrence.EvidenceJson,
                definition.Id,
                definition.Version,
                instance.Persona.Name,
                instance.Persona),
            new WorkModelPin(selection.CatalogKey, selection.ProviderAlias, selection.ModelId, selection.ReasoningEffort),
            WorkLimits.DefaultMaxAttempts,
            now);
    }
}
