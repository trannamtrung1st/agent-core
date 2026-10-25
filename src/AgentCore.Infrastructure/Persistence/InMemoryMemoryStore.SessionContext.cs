using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Spaces;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class InMemoryMemoryStore
{
    internal SessionSnapshot SwitchTenantQuiescentContext(TenantContextSwitch request, SessionContextSelection selection, DateTimeOffset at)
    {
        lock (_gate)
        {
            var current = _sessions[request.Owner.SessionId];
            if (current.Revision != request.ExpectedSessionRevision || current.ContextSelection?.Revision != request.ExpectedSelectionRevision) throw new SecurityConflictException("stale_revision");
            if (AgentRuns.Runs.Values.Any(r => r.SessionId == current.SessionId && r.Status == AgentRunStatus.Running)) throw new SecurityConflictException("session_in_use");
            var snapshot = current with { ContextSelection = selection, Revision = checked(current.Revision + 1), UpdatedAt = at };
            Store(snapshot); return snapshot;
        }
    }
}
