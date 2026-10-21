using AgentCore.Application.Events;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime
{
    private bool _requestIsolationRecheckPending;
    private sealed record RequestIsolationRecheck(EventContext Context) : SessionInput(Context);

    private void RecheckRequestIsolationAfterSettlement()
    {
        if (_requestIsolationRecheckPending) return;
        _requestIsolationRecheckPending = true;
        var context = NewContext();
        // A bounded mailbox timer retains this request while children, receipts or concrete
        // resource owners settle. It never starts model work or changes a durable owner.
        _ = EnqueueAfterDelayAsync();
        async Task EnqueueAfterDelayAsync()
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), _time, _lifetime.Token).ConfigureAwait(false);
                BeginWork();
                Enqueue(new RequestIsolationRecheck(context), urgent: true);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        }
    }

    private async Task TryFinalizeRequestIsolationAsync(EventContext context, CancellationToken ct)
    {
        // Tenant settlement belongs to the same host's quiescent reservation after this
        // generation releases. A completed Run must never regain an active write lease.
        if (_runtimeOwner is not null) return;
        if (!RequestIsolation.Required(_snapshot.Definition) || _snapshot.DurableLastEntrySequence == 0
            || LifecycleTransition.IsTerminal(_snapshot) || _snapshot.Status == SessionStatus.Paused
            || _activeResponseId is not null && !_responseTerminal || _agentRunAdmissionPending || HasPendingUserBatch()) return;
        var runs = await _agentRuns.ListForSessionAsync(RunOwner, SessionId, ct);
        if (runs.Count == 0 || runs.Any(r => !r.IsTerminal)) return;
        if (runs.Any(r => r.SideEffect.Disposition is AgentRunSideEffectDisposition.Prepared
            or AgentRunSideEffectDisposition.InFlight or AgentRunSideEffectDisposition.Indeterminate))
        { RecheckRequestIsolationAfterSettlement(); return; }
        var children = await _agentRuns.ListBackgroundChildrenAsync(RunOwner, SessionId, 100, ct);
        if (children.Count == 100 || children.Any(c => !c.Run.IsTerminal))
        { RecheckRequestIsolationAfterSettlement(); return; }
        foreach (var child in children.Where(c => c.Session.Origin.MayReportCompletion(c.Run.AgentRunId)))
            if (!await _agentRuns.HasCompletionReceiptAsync(RunOwner, child.Run.AgentRunId, ct))
            { RecheckRequestIsolationAfterSettlement(); return; }
        if (!await _tools.FinalizeRequestResourcesAsync(SessionId, ct))
        { RecheckRequestIsolationAfterSettlement(); return; }
        // Response and outcome are durable and their terminal output has already been published.
        ClearActive();
        await TerminalizeAsync(context, SessionLifecycleStatus.Completed, LifecycleTransitionSource.System,
            "request_completed", null, ct);
    }
}
