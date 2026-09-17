using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Persistence;

internal static class MemoryStoreSemantics
{
    public static bool SameContent(SessionSnapshot left, SessionSnapshot right) =>
        left.Status == right.Status
        && left.Mode == right.Mode
        && left.PendingMode == right.PendingMode
        && left.Summary == right.Summary
        && left.SummarizedThroughEntrySequence == right.SummarizedThroughEntrySequence
        && left.PendingTopic == right.PendingTopic
        && left.ProfileId == right.ProfileId
        && left.Definition.Id == right.Definition.Id
        && left.Definition.Version == right.Definition.Version
        && left.Title == right.Title
        && left.RuntimeEpoch == right.RuntimeEpoch
        && left.LastUserActivityAt == right.LastUserActivityAt
        && left.PauseReason == right.PauseReason
        && left.WorkspaceOwned == right.WorkspaceOwned
        && left.ArchivedAt == right.ArchivedAt
        && left.DurablyDeletedAt == right.DurablyDeletedAt
        && left.Entries.Count == right.Entries.Count
        && left.Entries.Zip(right.Entries).All(pair => pair.First == pair.Second);

    public static SessionSnapshot Recover(SessionSnapshot snapshot, DateTimeOffset now)
    {
        var entries = snapshot.Entries
            .Select(entry => entry.Status == EntryStatus.Streaming
                ? entry with { Status = EntryStatus.Interrupted }
                : entry)
            .ToArray();
        var status = snapshot.Status switch
        {
            SessionStatus.Attached => SessionStatus.Paused,
            SessionStatus.Ending => SessionStatus.Ended,
            _ => snapshot.Status
        };
        if (status == snapshot.Status
            && snapshot.PendingMode is null
            && entries.Zip(snapshot.Entries).All(pair => pair.First == pair.Second))
        {
            return snapshot;
        }

        var pauseReason = status == SessionStatus.Paused && snapshot.Status == SessionStatus.Attached
            ? "recovered"
            : snapshot.PauseReason;

        return snapshot with
        {
            Status = status,
            PendingMode = null,
            PauseReason = pauseReason,
            Entries = entries,
            Revision = snapshot.Revision + 1,
            UpdatedAt = now
        };
    }
}
