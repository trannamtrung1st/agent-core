using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class InMemoryMemoryStore
{
    internal TenantRequestIsolationPage RequestIsolationPage(Guid? after, int limit)
    {
        lock (_gate)
        {
            var page = tenantSessions.Values.Where(o => _sessions[o.SessionId].DurablyDeletedAt is null
                && _sessions[o.SessionId].LifecycleStatus == SessionLifecycleStatus.Active
                && (after is null || string.CompareOrdinal(o.SessionId.ToString("D"), after.Value.ToString("D")) > 0))
                .OrderBy(o => o.SessionId.ToString("D"), StringComparer.Ordinal).Take(limit + 1).ToArray();
            return new(page.Take(limit).ToArray(), page.Length > limit ? page[limit - 1].SessionId : null);
        }
    }
    internal TenantSessionSettlement? SessionSettlement(TenantSessionRoot owner)
    {
        lock (_gate)
        {
            var session = ReadRetainedTenantBackgroundSession(owner.TenantId, owner.SessionId);
            if (session?.Owner != owner) return null;
            var runs = AgentRuns.Runs.Values.Where(r => r.Owner.TenantId == owner.TenantId && r.SessionId == owner.SessionId).Take(101).ToArray();
            var children = tenantSessions.Values.Where(o => o.TenantId == owner.TenantId && _sessions[o.SessionId].Origin.OriginatingSessionId == owner.SessionId)
                .Take(101).Select(o => new TenantSession(o, _sessions[o.SessionId] with { Entries = [] })).ToArray();
            var childIds = children.Select(c => c.Owner.SessionId).ToHashSet();
            var childRuns = AgentRuns.Runs.Values.Where(r => r.Owner.TenantId == owner.TenantId && childIds.Contains(r.SessionId)).Take(1001).ToArray();
            var receipts = AgentRuns.CompletionReceipts.Values.Select(CompletionInboxMapping.Read)
                .Where(r => r.Owner.TenantId == owner.TenantId && r.ParentSessionId == owner.SessionId).Take(1001).ToArray();
            return new(session, runs, children, childRuns, receipts);
        }
    }
    internal TenantSession CompleteIsolatedRequest(TenantSession session, DateTimeOffset at)
    {
        lock (_gate)
        {
            var next = LifecycleTransition.Apply(session.Snapshot, SessionLifecycleStatus.Completed, LifecycleTransitionSource.System, at, "request_completed")
                with { Status = SessionStatus.Ended, PendingMode = null, Revision = checked(session.Snapshot.Revision + 1), UpdatedAt = at };
            Store(next); return new(session.Owner, next);
        }
    }
}
