namespace AgentCore.Domain.Conversation;

public static class HistoryRestoreWindow
{
    public const int PromptKeep = 20;

    public static IReadOnlyList<ConversationEntry> Select(IReadOnlyList<ConversationEntry> entries)
    {
        if (entries.Count == 0)
        {
            return [];
        }

        var ordered = entries.OrderBy(entry => entry.Sequence).ToArray();
        var suffix = TrailingUserSuffix.Of(ordered);
        var suffixStart = suffix.Count == 0 ? ordered[^1].Sequence + 1 : suffix[0].Sequence;
        var before = ordered.Where(entry => entry.Sequence < suffixStart).ToArray();
        var prompt = before.Length <= PromptKeep ? before : before.TakeLast(PromptKeep).ToArray();
        var streaming = ordered.Where(entry => entry.Status == EntryStatus.Streaming);
        return prompt
            .Concat(suffix)
            .Concat(streaming)
            .DistinctBy(entry => entry.EntryId)
            .OrderBy(entry => entry.Sequence)
            .ToArray();
    }
}
