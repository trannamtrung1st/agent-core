using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Memory;

namespace AgentCore.Infrastructure.Persistence;

public sealed class InMemoryStructuredMemoryStore : IStructuredMemoryStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, StructuredMemoryItem> _items = [];

    public ValueTask<StructuredMemoryItem?> FindAsync(
        Guid sessionId,
        Guid memoryId,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(
                _items.TryGetValue(memoryId, out var item) && item.SessionId == sessionId ? item : null);
        }
    }

    public ValueTask<StructuredMemoryItem?> FindActiveBySubjectAsync(
        Guid sessionId,
        MemoryKind kind,
        string subjectKey,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            StructuredMemoryItem? found = null;
            foreach (var item in _items.Values)
            {
                if (item.SessionId == sessionId
                    && item.Status == MemoryItemStatus.Active
                    && item.Kind == kind
                    && string.Equals(item.SubjectKey, subjectKey, StringComparison.Ordinal))
                {
                    found = item;
                    break;
                }
            }

            return ValueTask.FromResult(found);
        }
    }

    public ValueTask<int> CountActiveAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var count = 0;
            foreach (var item in _items.Values)
            {
                if (item.SessionId == sessionId && item.Status == MemoryItemStatus.Active)
                {
                    count++;
                }
            }

            return ValueTask.FromResult(count);
        }
    }

    public ValueTask<IReadOnlyList<StructuredMemoryItem>> ListActiveAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var items = new List<StructuredMemoryItem>();
            foreach (var item in _items.Values)
            {
                if (item.SessionId == sessionId && item.Status == MemoryItemStatus.Active)
                {
                    items.Add(item);
                }
            }

            return ValueTask.FromResult<IReadOnlyList<StructuredMemoryItem>>(items);
        }
    }

    public ValueTask InsertAsync(StructuredMemoryItem item, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            EnsureCapacity(item.SessionId);
            if (FindActiveUnlocked(item.SessionId, item.Kind, item.SubjectKey) is not null || _items.ContainsKey(item.MemoryId))
            {
                throw new AgentCoreException(
                    "Conflict",
                    "An active memory already uses this subject. Update that item.",
                    409);
            }

            _items[item.MemoryId] = item;
            return ValueTask.CompletedTask;
        }
    }

    public ValueTask SupersedeAsync(
        StructuredMemoryItem superseded,
        StructuredMemoryItem created,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_items.TryGetValue(superseded.MemoryId, out var current)
                || current.SessionId != superseded.SessionId
                || current.Status != MemoryItemStatus.Active)
            {
                throw AgentCoreErrors.NotFound("Memory was not found.");
            }

            var other = FindActiveUnlocked(created.SessionId, created.Kind, created.SubjectKey);
            if (other is not null && other.MemoryId != current.MemoryId)
            {
                throw new AgentCoreException(
                    "Conflict",
                    "An active memory already uses this subject. Update that item.",
                    409);
            }

            _items[superseded.MemoryId] = superseded;
            _items[created.MemoryId] = created;
            return ValueTask.CompletedTask;
        }
    }

    public ValueTask TombstoneAsync(StructuredMemoryItem tombstone, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_items.TryGetValue(tombstone.MemoryId, out var current)
                || current.SessionId != tombstone.SessionId
                || current.Status != MemoryItemStatus.Active)
            {
                throw AgentCoreErrors.NotFound("Memory was not found.");
            }

            _items[tombstone.MemoryId] = tombstone;
            return ValueTask.CompletedTask;
        }
    }

    public ValueTask DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var remove = new List<Guid>();
            foreach (var item in _items.Values)
            {
                if (item.SessionId == sessionId)
                {
                    remove.Add(item.MemoryId);
                }
            }

            foreach (var memoryId in remove)
            {
                _items.Remove(memoryId);
            }

            return ValueTask.CompletedTask;
        }
    }

    private void EnsureCapacity(Guid sessionId)
    {
        var count = 0;
        foreach (var item in _items.Values)
        {
            if (item.SessionId == sessionId && item.Status == MemoryItemStatus.Active)
            {
                count++;
            }
        }

        if (count >= MemoryLimits.MaxActiveItems)
        {
            throw new AgentCoreException("MemoryCapacity", "Active session memory is full.", 409);
        }
    }

    private StructuredMemoryItem? FindActiveUnlocked(Guid sessionId, MemoryKind kind, string subjectKey)
    {
        foreach (var item in _items.Values)
        {
            if (item.SessionId == sessionId
                && item.Status == MemoryItemStatus.Active
                && item.Kind == kind
                && string.Equals(item.SubjectKey, subjectKey, StringComparison.Ordinal))
            {
                return item;
            }
        }

        return null;
    }
}
