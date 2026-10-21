using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Workspaces;

public static class TenantWorkingCleanupRules
{
    public static TenantWorkingCleanupTarget? Target(TenantSessionSettlement graph, SandboxLease? lease)
    {
        var session = graph.Session;
        if (!graph.Complete || session.Snapshot.ProfileId is not null
            || (session.Snapshot.DurablyDeletedAt is null && !SessionLifecycle.IsTerminal(session.Snapshot.LifecycleStatus))
            || !TenantRequestIsolationRules.Settled(graph)
            || lease is not null && (!TenantRequestIsolationRules.LeaseOwner(session.Owner, lease) || lease.State != SandboxLeaseState.Terminated)) return null;
        var last = graph.Runs.OrderByDescending(r => r.CreatedAtUtc).ThenByDescending(r => r.AgentRunId).FirstOrDefault();
        // An admitted Run pins retention; later Instance edits cannot retroactively erase files.
        // An unused Session uses its own immutable creation pin.
        var definition = last is null ? session.Snapshot.Definition : last.Admission.Configuration?.Definition;
        if (session.Snapshot.DurablyDeletedAt is null && definition?.StateIsolationPolicy?.WorkingCleanup != WorkingCleanup.AfterTerminal) return null;
        return new(session.Owner, session.Snapshot.Revision, session.Snapshot.DurablyDeletedAt,
            last?.AgentRunId, last?.Revision, lease?.Generation, lease?.Revision);
    }
}
