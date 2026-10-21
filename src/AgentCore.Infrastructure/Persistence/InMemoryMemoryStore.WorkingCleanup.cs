using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class InMemoryMemoryStore
{
    internal TenantWorkingCleanupPage WorkingCleanupPage(Guid? after, int limit)
    {
        lock (_gate)
        {
            var rows = tenantSessions.Values.Where(o => (_sessions[o.SessionId].DurablyDeletedAt is not null
                || SessionLifecycle.IsTerminal(_sessions[o.SessionId].LifecycleStatus))
                && (after is null || string.CompareOrdinal(o.SessionId.ToString("D"), after.Value.ToString("D")) > 0))
                .OrderBy(o => o.SessionId.ToString("D"), StringComparer.Ordinal).Take(limit + 1).ToArray();
            return new(rows.Take(limit).ToArray(), rows.Length > limit ? rows[limit - 1].SessionId : null);
        }
    }
}
