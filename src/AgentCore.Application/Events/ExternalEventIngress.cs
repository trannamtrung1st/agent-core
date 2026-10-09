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
    ILogger<ExternalEventIngress>? logger = null, IEventFilterEvaluator? filters = null, ICoreEventStore? buckets = null)
{
    public async ValueTask<bool> CredentialsMatchAsync(
        string eventKey,
        string presentedToken,
        CancellationToken cancellationToken = default)
    {
        var source = await events.GetByKeyAsync(eventKey, cancellationToken).ConfigureAwait(false);
        return Authorized(source, presentedToken);
    }

    public async ValueTask<ExternalEventIngressResult> AdmitAsync(
        string eventKey,
        string presentedToken,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default)
    {
        var source = await events.GetByKeyAsync(eventKey, cancellationToken).ConfigureAwait(false);
        if (!Authorized(source, presentedToken))
        {
            Log(ExternalEventIngressKind.Unauthorized, eventKey, null, 0);
            return new ExternalEventIngressResult(ExternalEventIngressKind.Unauthorized, null, "unauthorized");
        }

        if (!ExternalEventEnvelope.TryNormalize(
                body.Span,
                out var evidence,
                out var sourceEventId,
                out var occurredAt,
                out var error))
        {
            Log(ExternalEventIngressKind.Invalid, eventKey, null, 0);
            return new ExternalEventIngressResult(ExternalEventIngressKind.Invalid, null, error);
        }

        var now = TriggerScheduleCalculator.Truncate(time.GetUtcNow());
        var occurred = occurredAt == DateTimeOffset.UnixEpoch ? now : TriggerScheduleCalculator.Truncate(occurredAt);
        var candidate = new ExternalEvent(
            ids.NewId(),
            source!.ResourceId,
            sourceEventId,
            occurred,
            now,
            evidence);
        var subscribers = await triggers.ListEventSubscriptionsAsync(source.ResourceId, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var targets = subscribers
            .Select(item => new ExternalEventTarget(item.AutomationId, item.Owner.AgentInstanceId, item.Owner.ProfileId, new(item.AutomationId, item.Owner, item.TriggerRevision, ((EventTrigger)item.Trigger).FilterExpression, ((EventTrigger)item.Trigger).Dispatch, TriggerSourceKind.ApplicationEvent, source.ResourceId)))
            .ToArray();
        var admitted = await events.AdmitAsync(candidate, targets, cancellationToken).ConfigureAwait(false);
        var created = await ResumeEventAsync(admitted.Event, now, cancellationToken).ConfigureAwait(false);
        var kind = admitted.Kind == ExternalEventAdmitKind.Duplicate
            ? ExternalEventIngressKind.Duplicate
            : ExternalEventIngressKind.Admitted;
        Log(kind, eventKey, admitted.Event.EventId, created);
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

    internal static string OccurrenceDedupeKey(Guid resourceId, string sourceEventId) =>
        $"event:{resourceId:D}:{sourceEventId}";

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
        var registration = await triggers.GetAsync(owner, delivery.AutomationId, cancellationToken).ConfigureAwait(false);
        var source = await events.GetAsync(stored.ResourceId, cancellationToken).ConfigureAwait(false);
        if (source?.Status != WebhookEventStatus.Active || registration is null || registration.Status != AutomationStatus.Active)
        {
            await events.MarkDeliveryAsync(stored.EventId, delivery.AutomationId, ExternalEventDeliveryStatus.Skipped, cancellationToken)
                .ConfigureAwait(false);
            return false;
        }

        var snapshot = delivery.Snapshot;
        var payload = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(stored.EvidenceJson);
        Guid? root = payload.TryGetProperty("rootAgentRunId", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.String && r.TryGetGuid(out var rg) ? rg : null;
        var depth = payload.TryGetProperty("triggerDepth", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.Number && d.TryGetInt32(out var dg) ? dg : 0;
        var envelope = System.Text.Json.JsonSerializer.SerializeToElement(new { schemaVersion = 1, id = stored.EventId,
            type = "webhook." + source.EventKey, occurredAtUtc = stored.OccurredAtUtc, receivedAtUtc = stored.AdmittedAtUtc,
            source = new { kind = "webhook", key = source.EventKey }, scope = new { agentInstanceId = delivery.AgentInstanceId },
            causation = new { rootAgentRunId = root, triggerDepth = depth },
            data = payload.GetProperty("data") });
        var decision = delivery.Decision ?? (snapshot is { ExpressionVersion: not "js-expression-v1" } ? new(null, "error", "filter-expression-version") : snapshot?.FilterExpression is null ? new EventFilterResult(true, "matched")
            : filters?.Evaluate(snapshot.FilterExpression, envelope, cancellationToken) ?? new(null, "error", "filter-unavailable"));
        await events.DecideDeliveryAsync(stored.EventId, delivery.AutomationId, decision, cancellationToken);
        AgentCore.Application.Observability.RuntimeTelemetry.RecordEventFilter("webhook", decision.Status);
        if (depth >= 4)
        { await events.MarkDeliveryAsync(stored.EventId, delivery.AutomationId, ExternalEventDeliveryStatus.Skipped, cancellationToken); return false; }
        if (decision.Matched != true)
        {
            await events.MarkDeliveryAsync(stored.EventId, delivery.AutomationId, decision.Matched == false ? ExternalEventDeliveryStatus.Filtered : ExternalEventDeliveryStatus.FilterError, cancellationToken);
            return false;
        }
        if (snapshot?.Dispatch.Mode == EventDispatchMode.CoalesceLatest)
        {
            var policy = await guard.EvaluateAsync(owner, TriggerSourceKind.ApplicationEvent, cancellationToken);
            if (buckets is null || policy.Kind != TriggerAdmissionDecisionKind.Allow || depth >= 4)
            { await events.MarkDeliveryAsync(stored.EventId, delivery.AutomationId, ExternalEventDeliveryStatus.Skipped, cancellationToken); return false; }
            await buckets.CoalesceAsync(new(stored.EventId, owner, source.EventKey, stored.AdmittedAtUtc, payload.GetProperty("data").GetRawText(), root, depth), snapshot, cancellationToken);
            await events.MarkDeliveryAsync(stored.EventId, delivery.AutomationId, ExternalEventDeliveryStatus.Coalesced, cancellationToken);
            return false;
        }
        var outcome = await TryCreateOccurrenceAsync(
            registration,
            stored.ResourceId,
            stored.SourceEventId,
            stored.EvidenceJson,
            stored.EventId,
            now,
            cancellationToken, snapshot?.TriggerRevision).ConfigureAwait(false);
        await events.MarkDeliveryAsync(stored.EventId, delivery.AutomationId, outcome.Status, cancellationToken)
            .ConfigureAwait(false);
        return outcome.Created;
    }

    private readonly record struct DeliveryOutcome(ExternalEventDeliveryStatus Status, bool Created);

    private async ValueTask<DeliveryOutcome> TryCreateOccurrenceAsync(
        Automation registration,
        Guid resourceId,
        string sourceEventId,
        string evidence,
        Guid eventId,
        DateTimeOffset now,
        CancellationToken cancellationToken, long? triggerRevision = null)
    {
        var decision = await guard.EvaluateAsync(registration.Owner, TriggerSourceKind.ApplicationEvent, cancellationToken)
            .ConfigureAwait(false);
        if (decision.Kind == TriggerAdmissionDecisionKind.Suspend)
        {
            return new DeliveryOutcome(ExternalEventDeliveryStatus.Skipped, false);
        }

        var pin = registration.ExecutionTarget.Kind == AutomationExecutionTargetKind.ExistingSession ? null : await ExecutionModelAdmission.ResolveAsync(
            catalog,
            instances,
            definitions,
            triggers,
            registration.Owner,
            registration.AutomationId,
            cancellationToken).ConfigureAwait(false);
        if (pin is { FailureCode: not null })
        {
            return new DeliveryOutcome(ExternalEventDeliveryStatus.Skipped, false);
        }

        string occurrenceEvidence;
        try
        {
            occurrenceEvidence = AutomationRules.Evidence(registration, new { sourceEventId, receiptId = eventId, eventId = resourceId, payload = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(evidence),
                causation = WebhookCausation(evidence, registration.AutomationId) });
        }
        catch (ArgumentException)
        {
            logger?.LogWarning("Event delivery {EventId} to Automation {AutomationId} exceeds the occurrence evidence budget.", eventId, registration.AutomationId);
            return new DeliveryOutcome(ExternalEventDeliveryStatus.Skipped, false);
        }
        var occurrence = new TriggerOccurrence(
            ids.NewId(),
            OccurrenceDedupeKey(resourceId, sourceEventId) + ":" + registration.AutomationId.ToString("D"),
            registration.AutomationId,
            registration.Owner,
            TriggerSourceKind.ApplicationEvent,
            null,
            now,
            now,
            occurrenceEvidence,
            eventId,
            triggerRevision ?? registration.TriggerRevision,
            OccurrenceRoutingDisposition.Pending,
            null,
            0,
            null,
            null,
            null,
            modelPin: pin?.Pin, executionTarget: registration.ExecutionTarget, completionDelivery: registration.CompletionDelivery);
        var admitted = await triggers.AdmitOccurrenceAsync(occurrence, cancellationToken).ConfigureAwait(false);
        return new DeliveryOutcome(ExternalEventDeliveryStatus.Admitted, admitted.Kind == TriggerOccurrenceAdmitKind.Admitted);
    }

    private static object WebhookCausation(string evidence, Guid automationId)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(evidence);
        var payload = doc.RootElement;
        Guid? root = payload.TryGetProperty("rootAgentRunId", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.String && r.TryGetGuid(out var id) ? id : null;
        var depth = payload.TryGetProperty("triggerDepth", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.Number && d.TryGetInt32(out var value) ? value : 0;
        return new { rootAgentRunId = root, triggerDepth = depth + 1, visitedAutomationIds = new[] { automationId } };
    }

    private static bool Authorized(WebhookEvent? source, string presentedToken) =>
        source is { Status: WebhookEventStatus.Active }
        && WebhookTokens.Matches(source.CredentialHash, presentedToken);

    private void Log(ExternalEventIngressKind kind, string eventKey, Guid? eventId, int subscribers) =>
        logger?.LogInformation(
            "External event {Admission} for source {EventKey} event {EventId} new subscribers {SubscriberCount}.",
            kind,
            eventKey,
            eventId,
            subscribers);
}
