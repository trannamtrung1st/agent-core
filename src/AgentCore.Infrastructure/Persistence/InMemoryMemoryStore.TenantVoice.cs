using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class InMemoryMemoryStore
{
    internal TenantSession ChangeTenantVoiceMode(TenantSessionRoot owner, long expectedRevision, SessionMode mode, DateTimeOffset at)
    {
        lock (_gate)
        {
            if (tenantSessions.GetValueOrDefault(owner.SessionId) != owner || !_sessions.TryGetValue(owner.SessionId, out var current) || current.DurablyDeletedAt is not null)
                throw new AccessDeniedException("resource_unavailable");
            if (current.Revision != expectedRevision) throw new SecurityConflictException("stale_revision");
            if (AgentRuns.Runs.Values.Any(r => r.SessionId == current.SessionId && r.Status == AgentRunStatus.Running))
                throw new SecurityConflictException("session_in_use");
            var changed = current with { Mode = mode, PendingMode = null, Revision = checked(current.Revision + 1), UpdatedAt = at };
            Store(changed); return new(owner, changed with { Entries = [] });
        }
    }
}
