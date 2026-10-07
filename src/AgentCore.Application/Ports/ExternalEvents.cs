using AgentCore.Domain.Events;

namespace AgentCore.Application.Ports;

public enum ExternalEventAdmitKind
{
    Admitted,
    Duplicate
}

public sealed record ExternalEventAdmit(ExternalEventAdmitKind Kind, ExternalEvent Event);

public interface IExternalEventStore
{
    ValueTask<ExternalEventSource> CreateAsync(
        ExternalEventSource source,
        CancellationToken cancellationToken = default);

    ValueTask<ExternalEventSource?> GetAsync(
        Guid sourceId,
        CancellationToken cancellationToken = default);

    ValueTask<ExternalEventSource?> GetByKeyAsync(
        Guid sourceKey,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<ExternalEventSource>> ListAsync(
        CancellationToken cancellationToken = default);

    ValueTask<ExternalEventSource> SaveAsync(
        ExternalEventSource source,
        long expectedRevision,
        CancellationToken cancellationToken = default);

    ValueTask<ExternalEventAdmit> AdmitAsync(
        ExternalEvent candidate,
        IReadOnlyList<ExternalEventTarget> targets,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<ExternalEventDelivery>> ListPendingDeliveriesAsync(
        Guid? eventId,
        int limit,
        CancellationToken cancellationToken = default);

    ValueTask MarkDeliveryAsync(
        Guid eventId,
        Guid automationId,
        ExternalEventDeliveryStatus status,
        CancellationToken cancellationToken = default);

    ValueTask<ExternalEvent?> GetByEventIdAsync(
        Guid eventId,
        CancellationToken cancellationToken = default);

    ValueTask<ExternalEvent?> GetEventAsync(
        Guid sourceId,
        string sourceEventId,
        CancellationToken cancellationToken = default);
}
