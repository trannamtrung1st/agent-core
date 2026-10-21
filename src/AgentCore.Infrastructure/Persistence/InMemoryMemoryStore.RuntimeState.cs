using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class InMemoryMemoryStore
{
    private IEnumerable<SessionSnapshot> StateSessions(TenantRuntimeStateQuery query) => tenantSessions.Values
        .Where(o => o.TenantId == query.Owner.TenantId && o.AgentInstanceId == query.Owner.AgentInstanceId && o.ExecutorPrincipalId == query.Owner.ExecutorPrincipalId
            && (query.Sessions is null || query.Sessions.Contains(o.SessionId)))
        .Select(o => _sessions.GetValueOrDefault(o.SessionId)).OfType<SessionSnapshot>()
        .Where(s => s.DurablyDeletedAt is null && s.ArchivedAt is null);
    internal DurableSessionState ObserveTenantSessions(TenantRuntimeStateQuery query)
    {
        lock (_gate)
        {
            var rows = StateSessions(query).ToArray();
            return new(rows.Count(s => s.LifecycleStatus == SessionLifecycleStatus.Active), rows.Count(s => s.LifecycleStatus == SessionLifecycleStatus.Paused),
                rows.Count(s => SessionLifecycle.IsTerminal(s.LifecycleStatus)), rows.Length == 0 ? null : rows.Max(s => s.UpdatedAt));
        }
    }
    internal DurableRunState ObserveTenantRuns(TenantRuntimeStateQuery query)
    {
        lock (_gate)
        {
            var sessions = StateSessions(query).Select(s => s.SessionId).ToHashSet();
            var rows = AgentRuns.Runs.Values.Where(r => r.Owner == query.Owner && sessions.Contains(r.SessionId)
                && (query.Runs is null || query.Runs.Contains(r.AgentRunId))).ToArray();
            int Count(AgentRunStatus state) => rows.Count(r => r.Status == state);
            return new(Count(AgentRunStatus.Queued), Count(AgentRunStatus.Running), Count(AgentRunStatus.WaitingForApproval),
                Count(AgentRunStatus.WaitingForSignal), Count(AgentRunStatus.WaitingToRetry), rows.Count(r => r.IsTerminal));
        }
    }
}
