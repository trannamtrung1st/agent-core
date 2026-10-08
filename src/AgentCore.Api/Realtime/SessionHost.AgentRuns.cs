using AgentCore.Application.Sessions;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;

namespace AgentCore.Api.Realtime;

public sealed partial class SessionHost
{
    public async Task<AgentRun> ControlAgentRunAsync(Guid sessionId, Guid runId, long revision, Guid? approvalId = null,
        long? approvalRevision = null, string? actionHash = null, AgentRunApprovalDecision? decision = null, CancellationToken ct = default)
    {
        AgentRun? result = null;
        var accepted = await DispatchIntoSessionAsync(sessionId, null, ct, operation: async (runtime, token) =>
        { result = await runtime.ControlAgentRunAsync(runId, revision, approvalId, approvalRevision, actionHash, decision, token).ConfigureAwait(false); return true; }).ConfigureAwait(false);
        return accepted && result is not null ? result : throw AgentCoreErrors.Conflict("Session cannot accept this execution control.");
    }

    public ValueTask<bool> ContinueInChatAsync(Guid sessionId, CancellationToken ct = default) =>
        DispatchIntoSessionAsync(sessionId, null, ct, continueInChat: true);

    public ValueTask<bool> AdmitBackgroundCompletionAsync(BackgroundCompletionCandidate source, CancellationToken ct = default) =>
        DispatchIntoSessionAsync(source.Session.Origin.OriginatingSessionId!.Value, null, ct, source);

    public ValueTask<bool> DispatchAgentRunAsync(AgentRun run, CancellationToken cancellationToken = default) =>
        DispatchIntoSessionAsync(run.SessionId, run, cancellationToken);

    public ValueTask<bool> RepairPendingAgentInputsAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        DispatchIntoSessionAsync(sessionId, null, cancellationToken);

    private async ValueTask<bool> DispatchIntoSessionAsync(Guid sessionId, AgentRun? run, CancellationToken cancellationToken, BackgroundCompletionCandidate? completion = null, bool continueInChat = false, Func<SessionRuntime, CancellationToken, Task<bool>>? operation = null)
    {
        while (true)
        {
            if (_disposing.TryGetValue(sessionId, out var disposing))
            {
                await disposing.WaitAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!_live.TryGetValue(sessionId, out var live))
            {
                var snapshot = await _sessions.LoadRuntimeAsync(sessionId, cancellationToken).ConfigureAwait(false);
                if (!Eligible(snapshot, run, continueInChat)) return false;
                lock (_gate)
                {
                    if (_disposing.ContainsKey(sessionId)) continue;
                    if (_terminating.Contains(sessionId) || !_admitting) return false;
                    if (!_live.TryGetValue(sessionId, out live))
                    {
                        if (_live.Count >= Math.Max(1, _options.MaxActiveSessions)) return false;
                        live = new Live(_factory.Create(snapshot, this));
                        _live[sessionId] = live;
                    }
                }
            }

            bool accepted;
            bool detached;
            await live.Admission.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                lock (_gate)
                {
                    if (!_live.TryGetValue(sessionId, out var current) || !ReferenceEquals(current, live)) continue;
                    if (_terminating.Contains(sessionId) || !_admitting) return false;
                }
                if (!Eligible(live.Runtime.Snapshot, run, continueInChat)) return false;
                detached = live.ConnectionId is null;
                live.CancelHeadlessFinalize();
                accepted = operation is not null ? await operation(live.Runtime, cancellationToken).ConfigureAwait(false)
                    : continueInChat ? await live.Runtime.ContinueInChatAsync(cancellationToken).ConfigureAwait(false)
                    : completion is not null
                    ? await live.Runtime.AdmitBackgroundCompletionAsync(completion, cancellationToken).ConfigureAwait(false)
                    : run is null
                    ? await live.Runtime.RepairPendingAgentInputsAsync(detached, cancellationToken).ConfigureAwait(false)
                    : await live.Runtime.DispatchAgentRunAsync(run.AgentRunId, headless: detached, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                live.Admission.Release();
                // Cancellation of the dispatch caller does not revoke already admitted work.
                if (live.ConnectionId is null) _ = FinalizeDetachedSessionAsync(sessionId, live);
            }

            // Dispatch ACK transfers ownership; model execution must not occupy the coordinator loop.
            return accepted;
        }
    }

    private static bool Eligible(SessionSnapshot snapshot, AgentRun? run, bool continueInChat = false) =>
        (run is null || snapshot.SessionId == run.SessionId && snapshot.AgentInstanceId == run.AgentInstanceId
            && snapshot.ProfileId == run.ProfileId)
        && (continueInChat || snapshot.Status != SessionStatus.Paused || SessionPauseSemantics.IsTransportResumable(snapshot.PauseReason)) && snapshot.DurablyDeletedAt is null
        && snapshot.ArchivedAt is null && snapshot.Status is not (SessionStatus.Ended or SessionStatus.Ending)
        && !SessionLifecycle.IsTerminal(snapshot.LifecycleStatus);
}
