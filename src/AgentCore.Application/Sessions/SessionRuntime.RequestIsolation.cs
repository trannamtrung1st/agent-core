using AgentCore.Application.Events;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime
{
    private async Task TryFinalizeRequestIsolationAsync(EventContext context, CancellationToken ct)
    {
        if (!RequestIsolation.Required(_snapshot.Definition) || _snapshot.DurableLastEntrySequence == 0
            || LifecycleTransition.IsTerminal(_snapshot) || _snapshot.Status == SessionStatus.Paused
            || _activeResponseId is not null && !_responseTerminal || _agentRunAdmissionPending || HasPendingUserBatch()) return;
        var runs = await _agentRuns.ListForSessionAsync(RunOwner, SessionId, ct);
        if (runs.Count == 0 || runs.Any(r => !r.IsTerminal || r.SideEffect.Disposition is AgentRunSideEffectDisposition.Prepared
            or AgentRunSideEffectDisposition.InFlight or AgentRunSideEffectDisposition.Indeterminate)) return;
        var children = await _agentRuns.ListBackgroundChildrenAsync(RunOwner, SessionId, 100, ct);
        if (children.Count == 100 || children.Any(c => !c.Run.IsTerminal)) return;
        if (!await _tools.FinalizeRequestResourcesAsync(SessionId, ct)) return;
        // Response and outcome are durable and their terminal output has already been published.
        ClearActive();
        await TerminalizeAsync(context, SessionLifecycleStatus.Completed, LifecycleTransitionSource.System,
            "request_completed", null, ct);
    }
}
