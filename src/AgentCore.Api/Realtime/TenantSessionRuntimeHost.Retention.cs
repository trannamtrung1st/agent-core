using AgentCore.Application.Ports;

namespace AgentCore.Api.Realtime;

public sealed partial class TenantSessionRuntimeHost
{
    private TenantSessionRetentionTarget? retentionCursor;

    internal async ValueTask RecoverRetentionAsync(CancellationToken ct)
    {
        var targets = await inputSessions.ListDueSessionsAsync(retentionCursor, 100, ct).ConfigureAwait(false);
        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await RetireDueAsync(target, ct).ConfigureAwait(false))
                    logger.LogInformation("Retired due Session {SessionId} in tenant {TenantId}", target.Owner.SessionId, target.Owner.TenantId);
                else logger.LogInformation("Retention settlement deferred for Session {SessionId} in tenant {TenantId}", target.Owner.SessionId, target.Owner.TenantId);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                AgentCore.Application.Observability.HandledFailureLog.Warning(logger, error, "sessions.retention", "retention_session_failed",
                    new(TenantId: target.Owner.TenantId, SessionId: target.Owner.SessionId));
            }
            retentionCursor = target;
        }
        if (targets.Count < 100) retentionCursor = null;
    }

    internal async ValueTask<bool> RetireDueAsync(TenantSessionRetentionTarget target, CancellationToken ct)
    {
        Live? live; VoiceInput? voice;
        lock (_gate)
        {
            if (_stopping) return false;
            live = _live.GetValueOrDefault(target.Owner.SessionId);
            voice = _voices.GetValueOrDefault(target.Owner.SessionId);
            if (live is not null) StopTokens(live);
        }
        // No snapshot is written while its mailbox/voice lease is alive. Timeout only
        // defers cleanup; authoritative store expiry has already revoked access.
        try
        {
            if (live is not null) await live.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(10), time, ct).ConfigureAwait(false);
            if (voice is not null) await RetireVoiceAsync(voice.Lease).AsTask().WaitAsync(TimeSpan.FromSeconds(10), time, ct).ConfigureAwait(false);
        }
        catch (TimeoutException) { return false; }
        lock (_gate)
        {
            if (_stopping || _live.ContainsKey(target.Owner.SessionId) || _voices.ContainsKey(target.Owner.SessionId) || !_quiescentWrites.Add(target.Owner.SessionId)) return false;
            _sessionWriteEpoch = checked(_sessionWriteEpoch + 1);
        }
        try { return await inputSessions.RetireDueSessionAsync(target, ids.NewId(), ct).ConfigureAwait(false); }
        finally { lock (_gate) { _quiescentWrites.Remove(target.Owner.SessionId); _sessionWriteEpoch = checked(_sessionWriteEpoch + 1); } }
    }
}
