using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Observability;
using AgentCore.Domain.Conversation;

namespace AgentCore.Api.Realtime;

public sealed partial class TenantSessionRuntimeHost : ITenantSessionInputControl
{
    internal AgentCore.Application.Sessions.SessionRuntime ObserveInterruptionRuntime(Guid sessionId, Guid runId, Guid generation)
    {
        lock (_gate)
            return _live.TryGetValue(sessionId, out var live) && live.Run.AgentRunId == runId && live.Run.Claim?.Generation == generation
                ? live.Runtime : throw new InvalidOperationException("Exact live interruption generation is unavailable.");
    }

    public async ValueTask<TenantHumanInputCommit> SubmitAsync(TenantHumanInput input, CancellationToken ct = default)
    {
        while (true)
        {
            Live? live;
            lock (_gate)
            {
                if (_stopping) throw new AccessDeniedException("tenant_runtime_unavailable");
                live = _live.GetValueOrDefault(input.Owner.SessionId);
                if (live is null)
                {
                    if (!_quiescentWrites.Add(input.Owner.SessionId)) throw new SecurityConflictException("session_in_use");
                    _sessionWriteEpoch = checked(_sessionWriteEpoch + 1);
                }
            }
            if (live is null)
            {
                try { return await TenantHumanInputRules.CommitQuiescentAsync(input, inputSecurity, runs, inputSessions, ids, time, ct).ConfigureAwait(false); }
                finally { lock (_gate) { _quiescentWrites.Remove(input.Owner.SessionId); _sessionWriteEpoch = checked(_sessionWriteEpoch + 1); } }
            }
            if (live.Run.Owner != AgentRunOwner.ForTenant(input.Owner.TenantId, input.Owner.AgentInstanceId, input.Owner.ExecutorPrincipalId))
                throw new AccessDeniedException("tenant_context_mismatch");
            var current = await runs.FindTenantRunAsync(input.Owner.TenantId, live.Run.AgentRunId, ct).ConfigureAwait(false);
            if (current is { Status: AgentRunStatus.Running, CancellationRequested: false, Claim: { } claim }
                && claim.Generation == live.Run.Claim!.Generation && claim.LeaseExpiresAtUtc > time.GetUtcNow())
            {
                // Already-started Core reports and terminal/suspended Runtime generations
                // linearize before new human input. Queued reports still yield in the store.
                if (current.Admission.OutputContract != AgentRunOutputContract.CompletionReport || input.Admission.Interruption is not null)
                {
                    ct.ThrowIfCancellationRequested();
                    var pending = SubmitLiveInputAsync(live, input);
                    _ = ObserveInputAsync(pending);
                    if (await pending.WaitAsync(ct).ConfigureAwait(false) is { } committed) return committed;
                }
                await live.Stopped.Task.WaitAsync(ct).ConfigureAwait(false);
                var saved = await inputSessions.FindTenantSessionAsync(input.Owner.TenantId, input.Owner.SessionId, ct).ConfigureAwait(false)
                    ?? throw new AccessDeniedException("resource_unavailable");
                input = input with { Admission = input.Admission with { ExpectedSessionRevision = saved.Snapshot.Revision,
                    Input = input.Admission.Input with { Sequence = checked(saved.Snapshot.DurableLastEntrySequence + 1) } } };
                continue;
            }
            await CancelStaleLiveAsync(live, ct).ConfigureAwait(false);
            await live.Stopped.Task.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task<TenantHumanInputCommit?> SubmitLiveInputAsync(Live live, TenantHumanInput input)
    {
        // Once enqueued, the mailbox must finish its authorized write and retire a
        // stopped generation even when the HTTP/Hub caller no longer awaits its ACK.
        var committed = await live.Runtime.QueueTenantInputAsync(input).ConfigureAwait(false);
        if (committed?.Interrupted is not null) StopTokens(live);
        return committed;
    }
    private async Task ObserveInputAsync(Task pending)
    {
        try { await pending.ConfigureAwait(false); }
        catch (Exception ex) when (ex is AccessDeniedException or SecurityConflictException) { }
        catch (Exception ex) { DiagnosticLog.Warning(logger, ex, ids.NewId(), "Tenant input command failed."); }
    }
}
