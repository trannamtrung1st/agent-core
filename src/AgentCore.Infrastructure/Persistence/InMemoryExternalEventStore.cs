using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Events;

namespace AgentCore.Infrastructure.Persistence;

public sealed class InMemoryExternalEventStore : IExternalEventStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, ExternalEventSource> _sources = [];
    private readonly Dictionary<(Guid SourceId, string SourceEventId), ExternalEvent> _events = [];

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

    public ValueTask<ExternalEventAdmit> AdmitAsync(ExternalEvent candidate, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var key = (candidate.SourceId, candidate.SourceEventId);
            if (_events.TryGetValue(key, out var existing))
            {
                return ValueTask.FromResult(new ExternalEventAdmit(ExternalEventAdmitKind.Duplicate, existing));
            }

            _events[key] = candidate;
            return ValueTask.FromResult(new ExternalEventAdmit(ExternalEventAdmitKind.Admitted, candidate));
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
