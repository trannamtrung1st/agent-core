using System.Collections.Concurrent;
using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;

namespace AgentCore.Infrastructure.Admin;

public class InMemoryAdminEventStore(IIdGenerator ids) : IAdminEventStore
{
    private readonly ConcurrentDictionary<Guid, AdminEvent> _byOperationId = new();
    private readonly ConcurrentBag<AdminEvent> _events = [];

    public ValueTask<AdminEvent?> TryGetByOperationIdAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_byOperationId.TryGetValue(operationId, out var existing) ? existing : null);
    }

    public ValueTask<AdminEvent> AppendAsync(AdminEventAppend append, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AdminEventSummaryPolicy.ValidateAppend(append);
        if (_byOperationId.TryGetValue(append.OperationId, out var existing))
        {
            return ValueTask.FromResult(existing);
        }

        var eventId = ids.NewId();
        var created = new AdminEvent(
            eventId,
            append.OperationId,
            append.OccurredAt,
            append.ActorKind,
            append.Operation,
            append.TargetType,
            append.TargetId,
            append.Revision,
            append.Version,
            append.SummaryJson);
        if (!_byOperationId.TryAdd(append.OperationId, created))
        {
            return ValueTask.FromResult(_byOperationId[append.OperationId]);
        }

        _events.Add(created);
        return ValueTask.FromResult(created);
    }

    public ValueTask<IReadOnlyList<AdminEvent>> ListAsync(
        AdminEventListQuery query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var limit = Math.Clamp(query.Limit, 1, 500);
        IEnumerable<AdminEvent> rows = _events;
        if (!string.IsNullOrWhiteSpace(query.TargetType))
        {
            rows = rows.Where(item => item.TargetType == query.TargetType);
        }

        if (!string.IsNullOrWhiteSpace(query.TargetId))
        {
            rows = rows.Where(item => item.TargetId == query.TargetId);
        }

        var ordered = rows
            .OrderByDescending(item => item.OccurredAt)
            .ThenByDescending(item => item.EventId)
            .Take(limit)
            .ToArray();
        return ValueTask.FromResult<IReadOnlyList<AdminEvent>>(ordered);
    }

    internal virtual void AppendWithinLock(AdminEventAppend append)
    {
        _ = AppendAsync(append).AsTask().GetAwaiter().GetResult();
    }
}
