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
                _items.TryGetValue(memoryId, out var item)
                && item.Scope == MemoryScope.Session
                && item.SessionId == sessionId
                    ? item
                    : null);
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
                if (item.Scope == MemoryScope.Session
                    && item.SessionId == sessionId
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
                if (item.Scope == MemoryScope.Session
                    && item.SessionId == sessionId
                    && item.Status == MemoryItemStatus.Active)
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
                if (item.Scope == MemoryScope.Session
                    && item.SessionId == sessionId
                    && item.Status == MemoryItemStatus.Active)
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
            EnsureCapacity(item);
            if (ActiveConflict(item) is not null || _items.ContainsKey(item.MemoryId))
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

            var other = ActiveConflict(created);
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
                if (item.Scope == MemoryScope.Session && item.SessionId == sessionId)
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

    public ValueTask<StructuredMemoryItem?> FindIdentityUserAsync(
        Guid instanceId,
        Guid profileId,
        Guid memoryId,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(
                _items.TryGetValue(memoryId, out var item) && Owns(item, instanceId, profileId) ? item : null);
        }
    }

    public ValueTask<StructuredMemoryItem?> FindActiveIdentityUserBySubjectAsync(
        Guid instanceId,
        Guid profileId,
        MemoryKind kind,
        string subjectKey,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(FindIdentityUnlocked(instanceId, profileId, kind, subjectKey));
        }
    }

    public ValueTask<IReadOnlyList<StructuredMemoryItem>> ListActiveIdentityUserAsync(
        Guid instanceId,
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var items = new List<StructuredMemoryItem>();
            foreach (var item in _items.Values)
            {
                if (Owns(item, instanceId, profileId) && item.Status == MemoryItemStatus.Active)
                {
                    items.Add(item);
                }
            }

            return ValueTask.FromResult<IReadOnlyList<StructuredMemoryItem>>(items);
        }
    }

    public ValueTask<int> CountActiveIdentityUserAsync(
        Guid instanceId,
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var count = 0;
            foreach (var item in _items.Values)
            {
                if (Owns(item, instanceId, profileId) && item.Status == MemoryItemStatus.Active)
                {
                    count++;
                }
            }

            return ValueTask.FromResult(count);
        }
    }

    private void EnsureCapacity(StructuredMemoryItem candidate)
    {
        var count = 0;
        foreach (var item in _items.Values)
        {
            var sameOwner = candidate.Scope switch
            {
                MemoryScope.IdentityUser => Owns(item, candidate.OwnerInstanceId ?? Guid.Empty, candidate.OwnerProfileId ?? Guid.Empty)
                    && item.Status == MemoryItemStatus.Active,
                MemoryScope.User => OwnsUser(item, candidate.OwnerProfileId ?? Guid.Empty)
                    && item.Status == MemoryItemStatus.Active,
                _ => item.Scope == MemoryScope.Session
                    && item.SessionId == candidate.SessionId
                    && item.Status == MemoryItemStatus.Active
            };
            if (sameOwner)
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
            if (item.Scope == MemoryScope.Session
                && item.SessionId == sessionId
                && item.Status == MemoryItemStatus.Active
                && item.Kind == kind
                && string.Equals(item.SubjectKey, subjectKey, StringComparison.Ordinal))
            {
                return item;
            }
        }

        return null;
    }

    private StructuredMemoryItem? ActiveConflict(StructuredMemoryItem item) =>
        item.Scope switch
        {
            MemoryScope.IdentityUser => FindIdentityUnlocked(
                item.OwnerInstanceId ?? Guid.Empty,
                item.OwnerProfileId ?? Guid.Empty,
                item.Kind,
                item.SubjectKey),
            MemoryScope.User => FindUserUnlocked(item.OwnerProfileId ?? Guid.Empty, item.Kind, item.SubjectKey),
            _ => FindActiveUnlocked(item.SessionId, item.Kind, item.SubjectKey)
        };

    private StructuredMemoryItem? FindIdentityUnlocked(
        Guid instanceId,
        Guid profileId,
        MemoryKind kind,
        string subjectKey)
    {
        foreach (var item in _items.Values)
        {
            if (Owns(item, instanceId, profileId)
                && item.Status == MemoryItemStatus.Active
                && item.Kind == kind
                && string.Equals(item.SubjectKey, subjectKey, StringComparison.Ordinal))
            {
                return item;
            }
        }

        return null;
    }

    public ValueTask<StructuredMemoryItem?> FindUserAsync(
        Guid profileId,
        Guid memoryId,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(
                _items.TryGetValue(memoryId, out var item) && OwnsUser(item, profileId) ? item : null);
        }
    }

    public ValueTask<StructuredMemoryItem?> FindActiveUserBySubjectAsync(
        Guid profileId,
        MemoryKind kind,
        string subjectKey,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(FindUserUnlocked(profileId, kind, subjectKey));
        }
    }

    public ValueTask<IReadOnlyList<StructuredMemoryItem>> ListActiveUserAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var items = new List<StructuredMemoryItem>();
            foreach (var item in _items.Values)
            {
                if (OwnsUser(item, profileId) && item.Status == MemoryItemStatus.Active)
                {
                    items.Add(item);
                }
            }

            return ValueTask.FromResult<IReadOnlyList<StructuredMemoryItem>>(items);
        }
    }

    public ValueTask<int> CountActiveUserAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var count = 0;
            foreach (var item in _items.Values)
            {
                if (OwnsUser(item, profileId) && item.Status == MemoryItemStatus.Active)
                {
                    count++;
                }
            }

            return ValueTask.FromResult(count);
        }
    }

    private StructuredMemoryItem? FindUserUnlocked(Guid profileId, MemoryKind kind, string subjectKey)
    {
        foreach (var item in _items.Values)
        {
            if (OwnsUser(item, profileId)
                && item.Status == MemoryItemStatus.Active
                && item.Kind == kind
                && string.Equals(item.SubjectKey, subjectKey, StringComparison.Ordinal))
            {
                return item;
            }
        }

        return null;
    }

    private static bool Owns(StructuredMemoryItem item, Guid instanceId, Guid profileId) =>
        item.Scope == MemoryScope.IdentityUser
        && item.OwnerInstanceId == instanceId
        && item.OwnerProfileId == profileId;

    private static bool OwnsUser(StructuredMemoryItem item, Guid profileId) =>
        item.Scope == MemoryScope.User && item.OwnerProfileId == profileId;
}
