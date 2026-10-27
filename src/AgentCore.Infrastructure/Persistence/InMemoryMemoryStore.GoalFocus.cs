using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
namespace AgentCore.Infrastructure.Persistence;
public sealed partial class InMemoryMemoryStore
{
    internal void SaveQuiescentGoalFocus(SessionSnapshot next, long expectedRevision)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(next.SessionId, out var current) || current.Revision != expectedRevision) throw new SecurityConflictException("stale_revision");
            if (AgentRuns.Runs.Values.Any(r => r.SessionId == next.SessionId && r.Status == AgentRunStatus.Running)) throw new SecurityConflictException("session_in_use");
            Store(next);
        }
    }
}
