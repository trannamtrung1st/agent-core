using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Application.Sessions;

namespace AgentCore.Infrastructure.Persistence;

public sealed class InMemoryMemoryStore : IMemoryStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, SessionSnapshot> _sessions = [];
    private readonly Dictionary<Guid, UserProfile> _profiles = [];

    public ValueTask<SessionSnapshot?> LoadAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return _sessions.TryGetValue(sessionId, out var snapshot)
                ? ValueTask.FromResult<SessionSnapshot?>(Clone(snapshot))
                : ValueTask.FromResult<SessionSnapshot?>(null);
        }
    }

    public ValueTask SaveAsync(SessionSnapshot snapshot, long expectedRevision, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_sessions.TryGetValue(snapshot.SessionId, out var existing))
            {
                if (expectedRevision != 0 || snapshot.Revision != 1)
                {
                    throw AgentCoreErrors.Conflict("Insert requires expectedRevision 0 and snapshot.Revision 1.");
                }

                _sessions[snapshot.SessionId] = Clone(snapshot);
                return ValueTask.CompletedTask;
            }

            if (existing.Revision == snapshot.Revision && SameContent(existing, snapshot))
            {
                return ValueTask.CompletedTask;
            }

            if (existing.Revision != expectedRevision || snapshot.Revision != expectedRevision + 1)
            {
                throw AgentCoreErrors.Conflict("Stale session revision.");
            }

            _sessions[snapshot.SessionId] = Clone(snapshot);
            return ValueTask.CompletedTask;
        }
    }

    public ValueTask<IReadOnlyList<ConversationEntry>> ReadHistoryAsync(
        Guid sessionId,
        long afterEntrySequence,
        int limit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var snapshot))
            {
                return ValueTask.FromResult<IReadOnlyList<ConversationEntry>>([]);
            }

            var page = snapshot.Entries
                .Where(entry => entry.Sequence > afterEntrySequence)
                .Take(limit)
                .ToArray();
            return ValueTask.FromResult<IReadOnlyList<ConversationEntry>>(page);
        }
    }

    public ValueTask<UserProfile?> LoadProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return _profiles.TryGetValue(profileId, out var profile)
                ? ValueTask.FromResult<UserProfile?>(profile)
                : ValueTask.FromResult<UserProfile?>(null);
        }
    }

    public ValueTask SaveProfileAsync(UserProfile profile, long expectedRevision, CancellationToken cancellationToken = default)
    {
        LocalUserProfile.Validate(profile.Preferences);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_profiles.TryGetValue(profile.ProfileId, out var existing))
            {
                if (expectedRevision != 0)
                {
                    throw AgentCoreErrors.Conflict("Insert profile requires expectedRevision 0.");
                }

                _profiles[profile.ProfileId] = profile;
                return ValueTask.CompletedTask;
            }

            if (existing.Revision != expectedRevision)
            {
                throw AgentCoreErrors.Conflict("Stale profile revision.");
            }

            _profiles[profile.ProfileId] = profile;
            return ValueTask.CompletedTask;
        }
    }

    public ValueTask RecoverCrashedSessionsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            foreach (var id in _sessions.Keys.ToArray())
            {
                _sessions[id] = MemoryStoreSemantics.Recover(_sessions[id], DateTimeOffset.UtcNow);
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<SessionCatalogPage> ListCatalogAsync(
        string? cursor,
        int limit,
        bool includeArchived,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(CatalogCursor.Page(_sessions.Values, cursor, limit, includeArchived));
        }
    }

    private static SessionSnapshot Clone(SessionSnapshot snapshot) =>
        snapshot with { Entries = snapshot.Entries.ToArray() };

    private static bool SameContent(SessionSnapshot left, SessionSnapshot right) =>
        MemoryStoreSemantics.SameContent(left, right);
}
