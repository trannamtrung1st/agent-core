using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Sessions;

public static class HistoryPaging
{
    public static ConversationHistoryPage FromNewestFirst(IReadOnlyList<ConversationEntry> newestFirstPlusOne, int limit)
    {
        var hasOlder = newestFirstPlusOne.Count > limit;
        var newestFirst = hasOlder
            ? newestFirstPlusOne.Take(limit).ToArray()
            : newestFirstPlusOne as ConversationEntry[] ?? newestFirstPlusOne.ToArray();
        var items = newestFirst.Reverse().ToArray();
        var nextAfter = items.Length == 0 ? 0L : items[^1].Sequence;
        long? nextBefore = hasOlder && items.Length > 0 ? items[0].Sequence : null;
        return new ConversationHistoryPage(items, nextAfter, HasMore: false, hasOlder, nextBefore);
    }

    public static ConversationHistoryPage FromForward(IReadOnlyList<ConversationEntry> items, long after, int limit)
    {
        var nextAfter = items.Count == 0 ? after : items[^1].Sequence;
        return new ConversationHistoryPage(items, nextAfter, HasMore: items.Count == limit, HasOlder: false, NextBefore: null);
    }
}
