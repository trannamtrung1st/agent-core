using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Infrastructure.Persistence;

public sealed class InMemoryMemoryStore : IMemoryStore
{
    private readonly object _gate = new();

    internal object AdmissionGate => _gate;
    internal AgentRunMemoryState AgentRuns { get; } = new();

    internal ConversationEntry? FindEntry(Guid sessionId, Guid entryId)
    {
        lock (_gate) return _entries.TryGetValue(sessionId, out var entries)
            ? entries.SingleOrDefault(entry => entry.EntryId == entryId) : null;
    }
    private readonly Dictionary<Guid, SessionSnapshot> _sessions = [];
    private readonly Dictionary<Guid, List<ConversationEntry>> _entries = [];
    private readonly Dictionary<Guid, UserProfile> _profiles = [];

    internal void RemoveResponseDraft(Guid sessionId, Guid entryId)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(sessionId, out var entries)) entries.RemoveAll(entry => entry.EntryId == entryId);
        }
    }

    internal IReadOnlyList<Guid> PendingInputSessions(int limit)
    {
        lock (_gate) return _sessions.Values.Where(session => session.PendingAgentInputIds.Count > 0
                && session.DurablyDeletedAt is null && session.ArchivedAt is null
                && !SessionLifecycle.IsTerminal(session.LifecycleStatus)
                && session.Status is not (SessionStatus.Ended or SessionStatus.Ending)
                && (session.Status != SessionStatus.Paused || SessionPauseSemantics.IsTransportResumable(session.PauseReason))
                && !AgentRuns.Runs.Values.Any(run => run.SessionId == session.SessionId && !run.IsTerminal))
            .OrderBy(session => session.UpdatedAt).ThenBy(session => session.SessionId).Take(limit)
            .Select(session => session.SessionId).ToArray();
    }

    internal int CountLiveByInstance(Guid instanceId)
    {
        lock (_gate)
        {
            return _sessions.Values.Count(snapshot =>
                snapshot.DurablyDeletedAt is null && snapshot.AgentInstanceId == instanceId);
        }
    }

    internal int CountLiveByDefinition(string definitionId)
    {
        lock (_gate)
        {
            return _sessions.Values.Count(snapshot =>
                snapshot.DurablyDeletedAt is null
                && string.Equals(snapshot.Definition.Id, definitionId, StringComparison.Ordinal));
        }
    }

    public ValueTask<SessionSnapshot?> LoadAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return _sessions.TryGetValue(sessionId, out var snapshot)
                ? ValueTask.FromResult<SessionSnapshot?>(CloneWindow(snapshot, sessionId))
                : ValueTask.FromResult<SessionSnapshot?>(null);
        }
    }

    public ValueTask<SessionSnapshot?> LoadMetadataAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return _sessions.TryGetValue(sessionId, out var snapshot)
                ? ValueTask.FromResult<SessionSnapshot?>(CloneMeta(snapshot))
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

                Store(snapshot, replaceEntries: snapshot.DurablyDeletedAt is not null);
                return ValueTask.CompletedTask;
            }

            var storedWindow = CloneForCompare(existing, snapshot.SessionId, snapshot.Entries);
            if (existing.Revision == snapshot.Revision && SameContent(storedWindow, snapshot))
            {
                return ValueTask.CompletedTask;
            }

            if (existing.Revision != expectedRevision || snapshot.Revision != expectedRevision + 1)
            {
                throw AgentCoreErrors.Conflict("Stale session revision.");
            }

            if (existing.Origin != snapshot.Origin)
                throw AgentCoreErrors.Conflict("Session origin is immutable.");
            Store(snapshot, replaceEntries: snapshot.DurablyDeletedAt is not null);
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
            if (!_entries.TryGetValue(sessionId, out var entries))
            {
                return ValueTask.FromResult<IReadOnlyList<ConversationEntry>>([]);
            }

            var page = entries
                .Where(entry => entry.Sequence > afterEntrySequence)
                .OrderBy(entry => entry.Sequence)
                .Take(limit)
                .ToArray();
            return ValueTask.FromResult<IReadOnlyList<ConversationEntry>>(page);
        }
    }

    public ValueTask<ConversationHistoryPage?> ReadHistoryPageAsync(
        Guid sessionId,
        long? afterEntrySequence,
        long? beforeEntrySequence,
        int limit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var snapshot) || snapshot.DurablyDeletedAt is not null)
            {
                return ValueTask.FromResult<ConversationHistoryPage?>(null);
            }

            var entries = _entries.TryGetValue(sessionId, out var list)
                ? list
                : (IReadOnlyList<ConversationEntry>)[];
            if (afterEntrySequence is { } after)
            {
                var forward = entries
                    .Where(entry => entry.Sequence > after)
                    .OrderBy(entry => entry.Sequence)
                    .Take(limit)
                    .ToArray();
                return ValueTask.FromResult<ConversationHistoryPage?>(HistoryPaging.FromForward(forward, after, limit));
            }

            var filtered = beforeEntrySequence is { } before
                ? entries.Where(entry => entry.Sequence < before)
                : entries;
            var newestFirst = filtered
                .OrderByDescending(entry => entry.Sequence)
                .Take(limit + 1)
                .ToArray();
            return ValueTask.FromResult<ConversationHistoryPage?>(HistoryPaging.FromNewestFirst(newestFirst, limit));
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
                var snapshot = Full(id);
                var ownedResponses = AgentRuns.Runs.Values.Where(run => run.SessionId == id && !run.IsTerminal)
                    .Select(run => run.ResponseId).OfType<Guid>().ToHashSet();
                var recovered = MemoryStoreSemantics.Recover(snapshot, DateTimeOffset.UtcNow, ownedResponses);
                Store(recovered, replaceEntries: true);
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<SessionSnapshot>> ListOwnedSessionsAsync(
        Guid instanceId, Guid profileId, int limit, bool activeOnly = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            return ValueTask.FromResult<IReadOnlyList<SessionSnapshot>>(_sessions.Values
                .Where(s => s.AgentInstanceId == instanceId && s.ProfileId == profileId && s.DurablyDeletedAt is null
                    && (!activeOnly || s.Status == SessionStatus.Attached && s.LifecycleStatus == SessionLifecycleStatus.Active))
                .OrderByDescending(s => s.UpdatedAt).ThenByDescending(s => s.SessionId)
                .Take(Math.Clamp(limit, 1, 100)).Select(CloneMeta).ToArray());
    }

    public ValueTask<IReadOnlyList<SessionSnapshot>> ListOwnedActivePageAsync(Guid instanceId, Guid profileId,
        Guid? afterId, int limit, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        lock (_gate) return ValueTask.FromResult<IReadOnlyList<SessionSnapshot>>(_sessions.Values
            .Where(s => s.AgentInstanceId == instanceId && s.ProfileId == profileId && s.DurablyDeletedAt is null
                && s.Status == SessionStatus.Attached && s.LifecycleStatus == SessionLifecycleStatus.Active
                && (afterId is null || string.CompareOrdinal(s.SessionId.ToString("D"), afterId.Value.ToString("D")) > 0))
            .OrderBy(s => s.SessionId.ToString("D"), StringComparer.Ordinal).Take(limit).Select(CloneMeta).ToArray());
    }

    public ValueTask<SessionCatalogPage> ListBackgroundSessionsAsync(AgentRunOwner owner, string? cursor, int limit,
        bool includeArchived = false, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var items = _sessions.Values.Where(session => session.AgentInstanceId == owner.AgentInstanceId && session.ProfileId == owner.ProfileId
                && session.Surfaces.HasFlag(SessionSurface.BackgroundWork)).Select(CloneMeta).ToArray();
            return ValueTask.FromResult(CatalogCursor.Page(items, cursor, limit, includeArchived));
        }
    }

    public ValueTask<SessionCatalogPage> ListInstanceSessionsAsync(AgentRunOwner owner, string? cursor, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var items = _sessions.Values.Where(session => session.AgentInstanceId == owner.AgentInstanceId && session.ProfileId == owner.ProfileId).Select(CloneMeta).ToArray();
            return ValueTask.FromResult(CatalogCursor.Page(items, cursor, limit, true));
        }
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
            var items = _sessions.Values.Where(session => session.Surfaces.HasFlag(SessionSurface.ChatList)).Select(CloneMeta).ToArray();
            return ValueTask.FromResult(CatalogCursor.Page(items, cursor, limit, includeArchived));
        }
    }

    private void Store(SessionSnapshot snapshot, bool replaceEntries = false)
    {
        var merged = MergeEntries(snapshot.SessionId, snapshot.Entries, replaceEntries);
        _entries[snapshot.SessionId] = merged;
        _sessions[snapshot.SessionId] = snapshot with
        {
            Entries = [],
            LastEntrySequence = snapshot.DurableLastEntrySequence,
            LifecycleStatus = SessionLifecycle.Align(snapshot.Status, snapshot.LifecycleStatus),
            Purpose = snapshot.Purpose ?? SessionPurpose.OngoingDefault,
            CompletionPolicy = snapshot.CompletionPolicy ?? SessionCompletionPolicy.Default
        };
    }

    private List<ConversationEntry> MergeEntries(
        Guid sessionId,
        IReadOnlyList<ConversationEntry> incoming,
        bool replace)
    {
        if (replace)
        {
            return incoming.OrderBy(entry => entry.Sequence).ToList();
        }

        if (!_entries.TryGetValue(sessionId, out var existing))
        {
            return incoming.OrderBy(entry => entry.Sequence).ToList();
        }

        var byId = existing.ToDictionary(entry => entry.EntryId);
        foreach (var entry in incoming)
        {
            byId[entry.EntryId] = entry;
        }

        return byId.Values.OrderBy(entry => entry.Sequence).ToList();
    }

    private SessionSnapshot Full(Guid sessionId)
    {
        var meta = _sessions[sessionId];
        var entries = _entries.TryGetValue(sessionId, out var list) ? list.ToArray() : [];
        return meta with { Entries = entries };
    }

    private SessionSnapshot CloneWindow(SessionSnapshot snapshot, Guid sessionId)
    {
        var entries = _entries.TryGetValue(sessionId, out var list) ? list : (IReadOnlyList<ConversationEntry>)[];
        return snapshot with
        {
            Entries = HistoryRestoreWindow.Select(entries, snapshot.PendingAgentInputIds),
            LastEntrySequence = snapshot.LastEntrySequence
        };
    }

    private SessionSnapshot CloneMeta(SessionSnapshot snapshot) =>
        snapshot with { Entries = [] };

    private SessionSnapshot CloneForCompare(
        SessionSnapshot existing,
        Guid sessionId,
        IReadOnlyList<ConversationEntry> incoming)
    {
        var stored = _entries.TryGetValue(sessionId, out var list) ? list : [];
        var byId = stored.ToDictionary(entry => entry.EntryId);
        var matched = incoming
            .Select(entry => byId.TryGetValue(entry.EntryId, out var found) ? found : entry)
            .ToArray();
        return existing with
        {
            Entries = matched,
            LastEntrySequence = existing.LastEntrySequence
        };
    }

    private static bool SameContent(SessionSnapshot left, SessionSnapshot right) =>
        MemoryStoreSemantics.SameContent(left, right);
}
