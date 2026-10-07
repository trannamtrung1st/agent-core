using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Events;

namespace AgentCore.Infrastructure.Persistence;

public sealed class InMemoryExternalEventStore : IExternalEventStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, ExternalEventSource> _sources = [];
    private readonly Dictionary<(Guid SourceId, string SourceEventId), ExternalEvent> _events = [];
    private readonly Dictionary<(Guid EventId, Guid AutomationId), ExternalEventDelivery> _deliveries = [];

    public ValueTask<ExternalEventSource> CreateAsync(
        ExternalEventSource source,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_sources.Values.Any(item => item.SourceKey == source.SourceKey))
            {
                throw AgentCoreErrors.Conflict("Event source key is already in use.");
            }

            _sources[source.SourceId] = source;
            return ValueTask.FromResult(source);
        }
    }

    public ValueTask<ExternalEventSource?> GetAsync(Guid sourceId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_sources.GetValueOrDefault(sourceId));
        }
    }

    public ValueTask<ExternalEventSource?> GetByKeyAsync(Guid sourceKey, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_sources.Values.FirstOrDefault(item => item.SourceKey == sourceKey));
        }
    }

    public ValueTask<IReadOnlyList<ExternalEventSource>> ListAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult<IReadOnlyList<ExternalEventSource>>(
                _sources.Values.OrderBy(item => item.DisplayName).ThenBy(item => item.SourceId).ToArray());
        }
    }

    public ValueTask<ExternalEventSource> SaveAsync(
        ExternalEventSource source,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_sources.TryGetValue(source.SourceId, out var current) || current.Revision != expectedRevision)
            {
                throw AgentCoreErrors.Conflict("Event source revision is stale.");
            }

            _sources[source.SourceId] = source;
            return ValueTask.FromResult(source);
        }
    }

    public ValueTask<ExternalEventAdmit> AdmitAsync(
        ExternalEvent candidate,
        IReadOnlyList<ExternalEventTarget> targets,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var key = (candidate.SourceId, candidate.SourceEventId);
            if (_events.TryGetValue(key, out var existing))
            {
                return ValueTask.FromResult(new ExternalEventAdmit(ExternalEventAdmitKind.Duplicate, existing));
            }

            _events[key] = candidate;
            foreach (var target in targets)
            {
                var deliveryKey = (candidate.EventId, target.AutomationId);
                _deliveries[deliveryKey] = new ExternalEventDelivery(
                    candidate.EventId,
                    target.AutomationId,
                    target.AgentInstanceId,
                    target.ProfileId,
                    ExternalEventDeliveryStatus.Pending);
            }

            return ValueTask.FromResult(new ExternalEventAdmit(ExternalEventAdmitKind.Admitted, candidate));
        }
    }

    public ValueTask<IReadOnlyList<ExternalEventDelivery>> ListPendingDeliveriesAsync(
        Guid? eventId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var rows = _deliveries.Values
                .Where(item => item.Status == ExternalEventDeliveryStatus.Pending
                    && (eventId is null || item.EventId == eventId))
                .OrderBy(item => item.EventId)
                .ThenBy(item => item.AutomationId)
                .Take(Math.Max(1, limit))
                .ToArray();
            return ValueTask.FromResult<IReadOnlyList<ExternalEventDelivery>>(rows);
        }
    }

    public ValueTask MarkDeliveryAsync(
        Guid eventId,
        Guid automationId,
        ExternalEventDeliveryStatus status,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_deliveries.TryGetValue((eventId, automationId), out var current)
                && current.Status == ExternalEventDeliveryStatus.Pending)
            {
                _deliveries[(eventId, automationId)] = current with { Status = status };
            }

            return ValueTask.CompletedTask;
        }
    }

    public ValueTask<ExternalEvent?> GetByEventIdAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_events.Values.FirstOrDefault(item => item.EventId == eventId));
        }
    }

    public ValueTask<ExternalEvent?> GetEventAsync(
        Guid sourceId,
        string sourceEventId,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_events.GetValueOrDefault((sourceId, sourceEventId)));
        }
    }
}
