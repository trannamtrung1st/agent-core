using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Events;

namespace AgentCore.Infrastructure.Persistence;

public sealed class InMemoryExternalEventStore : IExternalEventStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, WebhookEvent> _sources = [];
    private readonly Dictionary<(Guid ResourceId, string SourceEventId), ExternalEvent> _events = [];
    private readonly Dictionary<(Guid EventId, Guid AutomationId), ExternalEventDelivery> _deliveries = [];

    public ValueTask<ExternalEventActivity> ReadActivityAsync(Guid resourceId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var receipts = _events.Values.Where(e => e.ResourceId == resourceId).OrderByDescending(e => e.AdmittedAtUtc).Take(20).ToArray();
            var ids = receipts.Select(e => e.EventId).ToHashSet();
            return ValueTask.FromResult(new ExternalEventActivity(receipts, _deliveries.Values.Where(d => ids.Contains(d.EventId)).ToArray()));
        }
    }

    public ValueTask<WebhookEvent> CreateAsync(
        WebhookEvent source,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_sources.ContainsKey(source.ResourceId) || _sources.Values.Any(item => item.EventKey == source.EventKey))
            {
                throw AgentCoreErrors.Conflict("Event key is already in use.");
            }

            _sources[source.ResourceId] = source;
            return ValueTask.FromResult(source);
        }
    }

    public ValueTask<WebhookEvent?> GetAsync(Guid resourceId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_sources.GetValueOrDefault(resourceId));
        }
    }

    public ValueTask<WebhookEvent?> GetByKeyAsync(string eventKey, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_sources.Values.FirstOrDefault(item => item.EventKey == eventKey));
        }
    }

    public ValueTask<IReadOnlyList<WebhookEvent>> ListAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult<IReadOnlyList<WebhookEvent>>(
                _sources.Values.OrderBy(item => item.DisplayName).ThenBy(item => item.ResourceId).ToArray());
        }
    }

    public ValueTask<WebhookEvent> SaveAsync(
        WebhookEvent source,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_sources.TryGetValue(source.ResourceId, out var current) || current.Revision != expectedRevision)
            {
                throw AgentCoreErrors.Conflict("Event revision is stale.");
            }

            if (source.EventKey != current.EventKey || source.CreatedAtUtc != current.CreatedAtUtc || source.Revision != expectedRevision + 1)
                throw AgentCoreErrors.Validation("Event identity and key are immutable and revisions must advance by one.");
            _sources[source.ResourceId] = source;
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
            var key = (candidate.ResourceId, candidate.SourceEventId);
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
                    ExternalEventDeliveryStatus.Pending, target.Snapshot);
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

    public ValueTask<EventFilterResult> DecideDeliveryAsync(Guid eventId, Guid automationId, EventFilterResult decision, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_deliveries.TryGetValue((eventId, automationId), out var d) || d.Status == ExternalEventDeliveryStatus.Skipped)
                return ValueTask.FromResult(new EventFilterResult(null, "error", "delivery-unavailable"));
            if (d.Decision is not null) return ValueTask.FromResult(d.Decision);
            _deliveries[(eventId, automationId)] = d with { Decision = decision };
            return ValueTask.FromResult(decision);
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
        Guid resourceId,
        string sourceEventId,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_events.GetValueOrDefault((resourceId, sourceEventId)));
        }
    }
}
