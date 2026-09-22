using AgentCore.Application.Observability;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Agents;

public static class SummaryBoundary
{
    public const string RejectedKind = "summary_boundary_rejected";

    public static long ResolveLastSequence(long lastEntrySequence, IReadOnlyList<ConversationEntry> history)
    {
        if (lastEntrySequence > 0)
        {
            return lastEntrySequence;
        }

        var last = 0L;
        foreach (var entry in history)
        {
            if (entry.Sequence > last)
            {
                last = entry.Sequence;
            }
        }

        return last;
    }

    public static bool IsValid(
        string? summary,
        long throughSequence,
        long lastEntrySequence,
        IReadOnlyList<ConversationEntry> history)
    {
        if (string.IsNullOrEmpty(summary) || throughSequence <= 0 || throughSequence > lastEntrySequence)
        {
            return false;
        }

        var suffix = TrailingUserSuffix.Of(history);
        return suffix.Count == 0 || throughSequence < suffix[0].Sequence;
    }

    public static IReadOnlyList<ConversationEntry> SelectHistory(
        string? summary,
        long throughSequence,
        long lastEntrySequence,
        IReadOnlyList<ConversationEntry> history,
        bool recordRejection = false)
    {
        var last = ResolveLastSequence(lastEntrySequence, history);
        if (IsValid(summary, throughSequence, last, history))
        {
            var selected = new List<ConversationEntry>(history.Count);
            foreach (var entry in history)
            {
                if (entry.Sequence > throughSequence)
                {
                    selected.Add(entry);
                }
            }

            return selected;
        }

        if (recordRejection && (!string.IsNullOrEmpty(summary) || throughSequence > 0))
        {
            RuntimeTelemetry.RecordDropped(RejectedKind);
        }

        return history;
    }
}
