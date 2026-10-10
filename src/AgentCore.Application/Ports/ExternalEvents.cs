using AgentCore.Domain.Events;

namespace AgentCore.Application.Ports;

public enum ExternalEventAdmitKind
{
    Admitted,
    Duplicate
}

public sealed record ExternalEventAdmit(ExternalEventAdmitKind Kind, ExternalEvent Event);

public sealed record ExternalEventActivity(IReadOnlyList<ExternalEvent> Receipts, IReadOnlyList<ExternalEventDelivery> Deliveries);

public sealed record ExternalEventRecoveryCursor(Guid EventId, Guid TriggerId);

public interface IExternalEventStore
{
    ValueTask<ExternalEventActivity> ReadActivityAsync(Guid resourceId, CancellationToken ct = default);

    ValueTask<WebhookEvent> CreateAsync(
        WebhookEvent source,
        CancellationToken cancellationToken = default);

    ValueTask<WebhookEvent?> GetAsync(
        Guid resourceId,
        CancellationToken cancellationToken = default);

    ValueTask<WebhookEvent?> GetByKeyAsync(
        string eventKey,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<WebhookEvent>> ListAsync(
        CancellationToken cancellationToken = default);

    ValueTask<WebhookEvent> SaveAsync(
        WebhookEvent source,
        long expectedRevision,
        CancellationToken cancellationToken = default);

    ValueTask<ExternalEventAdmit> AdmitAsync(
        ExternalEvent candidate,
        IReadOnlyList<ExternalEventTarget> targets,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<ExternalEventDelivery>> ListPendingDeliveriesAsync(
        Guid? eventId,
        int limit,
        CancellationToken cancellationToken = default, ExternalEventRecoveryCursor? after = null);

    ValueTask<EventFilterResult> DecideDeliveryAsync(Guid eventId, Guid triggerId, EventFilterResult decision, CancellationToken ct = default, EventFilterResult? expectedDecision = null);

    ValueTask MarkDeliveryAsync(
        Guid eventId,
        Guid triggerId,
        ExternalEventDeliveryStatus status,
        CancellationToken cancellationToken = default);

    ValueTask<ExternalEvent?> GetByEventIdAsync(
        Guid eventId,
        CancellationToken cancellationToken = default);

    ValueTask<ExternalEvent?> GetEventAsync(
        Guid resourceId,
        string sourceEventId,
        CancellationToken cancellationToken = default);
}
