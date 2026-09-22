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
        && left.SummaryFormatVersion == right.SummaryFormatVersion
        && left.SummaryGeneratedAt == right.SummaryGeneratedAt
        && left.SummaryModel == right.SummaryModel
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
        && left.DurableLastEntrySequence == right.DurableLastEntrySequence
        && SessionLifecycle.Align(left.Status, left.LifecycleStatus)
            == SessionLifecycle.Align(right.Status, right.LifecycleStatus)
        && PurposeEquals(left.Purpose, right.Purpose)
        && PolicyEquals(left.CompletionPolicy, right.CompletionPolicy)
        && left.LifecycleReason == right.LifecycleReason
        && left.LifecycleSource == right.LifecycleSource
        && left.LifecycleChangedAt == right.LifecycleChangedAt
        && left.SpeechLocaleOverride == right.SpeechLocaleOverride
        && left.ModelSelection == right.ModelSelection
        && IncomingEntriesMatch(left.Entries, right.Entries);

    private static bool PurposeEquals(SessionPurpose? left, SessionPurpose? right)
    {
        var a = left ?? SessionPurpose.OngoingDefault;
        var b = right ?? SessionPurpose.OngoingDefault;
        if (a.Kind != b.Kind || a.Description != b.Description || a.DeadlineAt != b.DeadlineAt)
        {
            return false;
        }

        return MetadataEquals(a.Metadata, b.Metadata);
    }

    private static bool MetadataEquals(
        IReadOnlyDictionary<string, string>? left,
        IReadOnlyDictionary<string, string>? right)
    {
        var a = left ?? new Dictionary<string, string>();
        var b = right ?? new Dictionary<string, string>();
        if (a.Count != b.Count)
        {
            return false;
        }

        foreach (var pair in a)
        {
            if (!b.TryGetValue(pair.Key, out var value) || value != pair.Value)
            {
                return false;
            }
        }

        return true;
    }

    private static bool PolicyEquals(SessionCompletionPolicy? left, SessionCompletionPolicy? right)
    {
        var a = left ?? SessionCompletionPolicy.Default;
        var b = right ?? SessionCompletionPolicy.Default;
        return a.AgentCompletion == b.AgentCompletion
            && a.UserCompletionAllowed == b.UserCompletionAllowed
            && a.UserCancellationAllowed == b.UserCancellationAllowed;
    }

    private static bool IncomingEntriesMatch(
        IReadOnlyList<ConversationEntry> stored,
        IReadOnlyList<ConversationEntry> incoming)
    {
        if (incoming.Count == 0)
        {
            return true;
        }

        var byId = stored.ToDictionary(entry => entry.EntryId);
        foreach (var entry in incoming)
        {
            if (!byId.TryGetValue(entry.EntryId, out var existing) || existing != entry)
            {
                return false;
            }
        }

        return true;
    }

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
        var lifecycleChanged = status != snapshot.Status;
        var lifecycle = SessionLifecycle.Align(status, snapshot.LifecycleStatus);
        var reason = snapshot.LifecycleReason;
        var source = snapshot.LifecycleSource;
        var changedAt = snapshot.LifecycleChangedAt;
        if (lifecycleChanged && snapshot.Status == SessionStatus.Ending && status == SessionStatus.Ended)
        {
            reason ??= "ending-recovery";
            source ??= LifecycleTransitionSource.System;
            changedAt = now;
        }
        else if (lifecycleChanged)
        {
            source ??= LifecycleTransitionSource.System;
            changedAt = now;
            reason ??= pauseReason;
        }

        return snapshot with
        {
            Status = status,
            LifecycleStatus = lifecycle,
            LifecycleReason = reason,
            LifecycleSource = source,
            LifecycleChangedAt = changedAt,
            PendingMode = null,
            PauseReason = pauseReason,
            Entries = entries,
            Revision = snapshot.Revision + 1,
            UpdatedAt = now
        };
    }
}
