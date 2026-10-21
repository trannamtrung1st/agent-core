using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class InMemoryMemoryStore
{
    internal IReadOnlyList<AgentCore.Domain.Conversation.ConversationEntry> ReadTenantHistoricalWindow(Guid tenant, Guid session, long after, long through, int limit)
    {
        lock (_gate)
        {
            if (ReadTenantSession(tenant, session) is null) throw new AgentCore.Application.Security.AccessDeniedException("continuity_source_unavailable");
            return _entries.GetValueOrDefault(session, []).Where(e => e.Sequence > after && e.Sequence <= through && e.Status != AgentCore.Domain.Conversation.EntryStatus.Streaming)
                .OrderBy(e => e.Sequence).Take(limit + 1).ToArray();
        }
    }

    internal TenantConversationPage? ReadTenantConversation(Guid tenant, Guid session, long? after, long? before, int limit)
    {
        lock (_gate)
        {
            var saved = ReadTenantSession(tenant, session);
            if (saved is null) return null;
            var entries = _entries.GetValueOrDefault(session, []);
            var history = after is { } cursor
                ? HistoryPaging.FromForward(entries.Where(e => e.Sequence > cursor).OrderBy(e => e.Sequence).Take(limit).ToArray(), cursor, limit)
                : HistoryPaging.FromNewestFirst(entries.Where(e => before is null || e.Sequence < before)
                    .OrderByDescending(e => e.Sequence).Take(limit + 1).ToArray(), limit);
            return new(saved.Owner, saved.Snapshot.Revision, history);
        }
    }
}
