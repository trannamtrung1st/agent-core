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
        var admitted = await events.AdmitAsync(candidate, cancellationToken).ConfigureAwait(false);
        if (admitted.Kind == ExternalEventAdmitKind.Duplicate)
        {
            Log(ExternalEventIngressKind.Duplicate, sourceKey, admitted.Event.EventId, 0);
            return new ExternalEventIngressResult(ExternalEventIngressKind.Duplicate, admitted.Event.EventId, null);
        }

        var subscribers = await triggers.ListEventSubscriptionsAsync(source.SourceId, eventType, cancellationToken)
            .ConfigureAwait(false);
        var created = 0;
        foreach (var registration in subscribers)
        {
            if (await TryCreateOccurrenceAsync(registration, sourceEventId, evidence, now, cancellationToken)
                    .ConfigureAwait(false))
            {
                created++;
            }
        }

        Log(ExternalEventIngressKind.Admitted, sourceKey, admitted.Event.EventId, created);
        return new ExternalEventIngressResult(ExternalEventIngressKind.Admitted, admitted.Event.EventId, null);
    }

    private async ValueTask<bool> TryCreateOccurrenceAsync(
        TriggerRegistration registration,
        string sourceEventId,
        string evidence,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var decision = await guard.EvaluateAsync(registration.Owner, TriggerSourceKind.ApplicationEvent, cancellationToken)
            .ConfigureAwait(false);
        if (decision.Kind == TriggerAdmissionDecisionKind.Suspend)
        {
            return false;
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
            return false;
        }

        var occurrence = new TriggerOccurrence(
            ids.NewId(),
            $"order.placed:{sourceEventId}",
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
        await triggers.AdmitOccurrenceAsync(occurrence, cancellationToken).ConfigureAwait(false);
        return true;
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
