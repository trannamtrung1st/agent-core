namespace AgentCore.Domain.Conversation;

public static class TrailingUserSuffix
{
    public static IReadOnlyList<ConversationEntry> Of(IReadOnlyList<ConversationEntry>? entries)
    {
        if (entries is null || entries.Count == 0)
        {
            return [];
        }

        var batch = new List<ConversationEntry>();
        for (var index = entries.Count - 1; index >= 0; index--)
        {
            var entry = entries[index];
            if (entry.Role == ConversationRole.User && entry.Status == EntryStatus.Completed)
            {
                batch.Add(entry);
                continue;
            }

            break;
        }

        if (batch.Count == 0)
        {
            return [];
        }

        batch.Reverse();
        return batch;
    }

    public static bool HasPending(IReadOnlyList<ConversationEntry>? entries) => Of(entries).Count > 0;
}
