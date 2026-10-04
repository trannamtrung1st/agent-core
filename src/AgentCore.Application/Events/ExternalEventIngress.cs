using AgentCore.Application.Connections;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Events;
using AgentCore.Domain.Triggers;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Events;

public enum ExternalEventIngressKind
{
    Admitted,
    Duplicate,
    Unauthorized,
    Invalid
}

public sealed record ExternalEventIngressResult(
    ExternalEventIngressKind Kind,
    Guid? EventId,
    string? Code);

public sealed class ExternalEventIngress(
    IExternalEventStore events,
    ITriggerStore triggers,
    ITriggerAdmissionGuard guard,
    IIdGenerator ids,
    TimeProvider time,
    IAgentInstanceStore instances,
    IAgentDefinitionStore definitions,
    IModelCatalog catalog,
    ILogger<ExternalEventIngress>? logger = null)
{
    public async ValueTask<bool> CredentialsMatchAsync(
        Guid sourceKey,
        string presentedToken,
        CancellationToken cancellationToken = default)
    {
        var source = await events.GetByKeyAsync(sourceKey, cancellationToken).ConfigureAwait(false);
        return Authorized(source, presentedToken);
    }

    public async ValueTask<ExternalEventIngressResult> AdmitAsync(
        Guid sourceKey,
        string presentedToken,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default)
    {
        var source = await events.GetByKeyAsync(sourceKey, cancellationToken).ConfigureAwait(false);
        if (!Authorized(source, presentedToken))
        {
            Log(ExternalEventIngressKind.Unauthorized, sourceKey, null, 0);
            return new ExternalEventIngressResult(ExternalEventIngressKind.Unauthorized, null, "unauthorized");
        }

        if (!ExternalEventEnvelope.TryNormalize(
                body.Span,
                out var evidence,
                out var sourceEventId,
                out var eventType,
                out var occurredAt,
                out var error))
        {
            Log(ExternalEventIngressKind.Invalid, sourceKey, null, 0);
            return new ExternalEventIngressResult(ExternalEventIngressKind.Invalid, null, error);
        }

        var now = TriggerScheduleCalculator.Truncate(time.GetUtcNow());
        var occurred = occurredAt == DateTimeOffset.UnixEpoch ? now : TriggerScheduleCalculator.Truncate(occurredAt);
        var candidate = new ExternalEvent(
            ids.NewId(),
            source!.SourceId,
            sourceEventId,
            eventType,
            occurred,
            now,
            evidence);
        var subscribers = await triggers.ListEventSubscriptionsAsync(source.SourceId, eventType, cancellationToken)
            .ConfigureAwait(false);
        var targets = subscribers
            .Select(item => new ExternalEventTarget(item.RegistrationId, item.Owner.AgentInstanceId, item.Owner.ProfileId))
            .ToArray();
        var admitted = await events.AdmitAsync(candidate, targets, cancellationToken).ConfigureAwait(false);
        var created = await ResumeEventAsync(admitted.Event, now, cancellationToken).ConfigureAwait(false);
        var kind = admitted.Kind == ExternalEventAdmitKind.Duplicate
            ? ExternalEventIngressKind.Duplicate
            : ExternalEventIngressKind.Admitted;
        Log(kind, sourceKey, admitted.Event.EventId, created);
        return new ExternalEventIngressResult(kind, admitted.Event.EventId, null);
    }

    public async ValueTask<int> ResumePendingAsync(CancellationToken cancellationToken = default)
    {
        var pending = await events.ListPendingDeliveriesAsync(null, 32, cancellationToken).ConfigureAwait(false);
        var created = 0;
        var now = TriggerScheduleCalculator.Truncate(time.GetUtcNow());
        foreach (var eventId in pending.Select(item => item.EventId).Distinct())
        {
            var stored = await events.GetByEventIdAsync(eventId, cancellationToken).ConfigureAwait(false);
            if (stored is null)
            {
                continue;
            }

            created += await ResumeEventAsync(stored, now, cancellationToken).ConfigureAwait(false);
        }

        return created;
    }

    internal static string OccurrenceDedupeKey(Guid sourceId, string sourceEventId) =>
        $"order.placed:{sourceId:D}:{sourceEventId}";

    private async ValueTask<int> ResumeEventAsync(
        ExternalEvent stored,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var pending = await events.ListPendingDeliveriesAsync(stored.EventId, 32, cancellationToken).ConfigureAwait(false);
        var created = 0;
        foreach (var delivery in pending)
        {
            if (await TryDeliverAsync(stored, delivery, now, cancellationToken).ConfigureAwait(false))
            {
                created++;
            }
        }

        return created;
    }

    private async ValueTask<bool> TryDeliverAsync(
        ExternalEvent stored,
        ExternalEventDelivery delivery,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var owner = new TriggerOwner(delivery.AgentInstanceId, delivery.ProfileId);
        var registration = await triggers.GetAsync(owner, delivery.RegistrationId, cancellationToken).ConfigureAwait(false);
        if (registration is null)
        {
            await events.MarkDeliveryAsync(stored.EventId, delivery.RegistrationId, ExternalEventDeliveryStatus.Skipped, cancellationToken)
                .ConfigureAwait(false);
            return false;
        }

        var outcome = await TryCreateOccurrenceAsync(
            registration,
            stored.SourceId,
            stored.SourceEventId,
            stored.EvidenceJson,
            now,
            cancellationToken).ConfigureAwait(false);
        await events.MarkDeliveryAsync(stored.EventId, delivery.RegistrationId, outcome.Status, cancellationToken)
            .ConfigureAwait(false);
        return outcome.Created;
    }

    private readonly record struct DeliveryOutcome(ExternalEventDeliveryStatus Status, bool Created);

    private async ValueTask<DeliveryOutcome> TryCreateOccurrenceAsync(
        TriggerRegistration registration,
        Guid sourceId,
        string sourceEventId,
        string evidence,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var decision = await guard.EvaluateAsync(registration.Owner, TriggerSourceKind.ApplicationEvent, cancellationToken)
            .ConfigureAwait(false);
        if (decision.Kind == TriggerAdmissionDecisionKind.Suspend)
        {
            return new DeliveryOutcome(ExternalEventDeliveryStatus.Skipped, false);
        }

        var pin = await ExecutionModelAdmission.ResolveAsync(
            catalog,
            instances,
            definitions,
            triggers,
            registration.Owner,
            registration.RegistrationId,
            cancellationToken).ConfigureAwait(false);
        if (pin is { FailureCode: not null })
        {
            return new DeliveryOutcome(ExternalEventDeliveryStatus.Skipped, false);
        }

        var occurrence = new TriggerOccurrence(
            ids.NewId(),
            OccurrenceDedupeKey(sourceId, sourceEventId),
            registration.RegistrationId,
            registration.Owner,
            TriggerSourceKind.ApplicationEvent,
            null,
            now,
            now,
            evidence,
            null,
            registration.ScheduleRevision,
            OccurrenceRoutingDisposition.Pending,
            null,
            0,
            null,
            null,
            null,
            null,
            pin?.Pin);
        var admitted = await triggers.AdmitOccurrenceAsync(occurrence, cancellationToken).ConfigureAwait(false);
        return new DeliveryOutcome(ExternalEventDeliveryStatus.Admitted, admitted.Kind == TriggerOccurrenceAdmitKind.Admitted);
    }

    private static bool Authorized(ExternalEventSource? source, string presentedToken) =>
        source is { Status: ExternalEventSourceStatus.Active }
        && WebhookTokens.Matches(source.CredentialHash, presentedToken);

    private void Log(ExternalEventIngressKind kind, Guid sourceKey, Guid? eventId, int subscribers) =>
        logger?.LogInformation(
            "External event {Admission} for source {SourceKey} event {EventId} new subscribers {SubscriberCount}.",
            kind,
            sourceKey,
            eventId,
            subscribers);
}
