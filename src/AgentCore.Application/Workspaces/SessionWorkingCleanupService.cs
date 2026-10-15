using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Workspaces;

/// <summary>Deferred cleanup at the existing terminal lifecycle boundary. Never deletes retained artifacts or home.</summary>
public sealed class SessionWorkingCleanupService(IMemoryStore sessions, IAgentRunStore runs, ISandboxStore sandboxes, ISessionWorkspace workspace)
{
    private string? _cursor;
    public async ValueTask<bool> CleanupAsync(Guid sessionId, CancellationToken ct = default)
    {
        var session = await sessions.LoadMetadataAsync(sessionId, ct);
        if (session is null || session.ProfileId is not { } profile || !SessionLifecycle.IsTerminal(session.LifecycleStatus)
            || session.DurablyDeletedAt is not null || session.PendingAgentInputIds.Count > 0) return false;
        var owner = new AgentRunOwner(session.AgentInstanceId, profile);
        var executions = await runs.ListForSessionAsync(owner, sessionId, ct);
        var last = executions.OrderByDescending(r => r.CreatedAtUtc).ThenByDescending(r => r.AgentRunId).FirstOrDefault();
        if (last?.Admission.Configuration?.Definition.StateIsolationPolicy?.WorkingCleanup != WorkingCleanup.AfterTerminal
            || executions.Any(r => !r.IsTerminal || r.SideEffect.Disposition is AgentRunSideEffectDisposition.Prepared
                or AgentRunSideEffectDisposition.InFlight or AgentRunSideEffectDisposition.Indeterminate)) return false;
        var children = await runs.ListBackgroundChildrenAsync(owner, sessionId, 100, ct);
        if (children.Count == 100 || children.Any(c => !c.Run.IsTerminal)) return false;
        var lease = await sandboxes.GetLeaseAsync(sessionId, ct);
        if (lease is not null && lease.State != SandboxLeaseState.Terminated) return false;
        // Terminal is irreversible. Admission cannot race this removal into another Run.
        await workspace.DeleteSessionAsync(sessionId, ct);
        return true;
    }

    public async ValueTask RecoverAsync(CancellationToken ct = default)
    {
        var page = await sessions.ListTerminalSessionsAsync(_cursor, 100, ct);
        foreach (var session in page.Items)
            if (SessionLifecycle.IsTerminal(session.LifecycleStatus)) await CleanupAsync(session.SessionId, ct);
        _cursor = page.NextCursor;
    }
}
