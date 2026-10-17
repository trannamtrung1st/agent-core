using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;

namespace AgentCore.Api.Realtime;

public sealed partial class TenantSessionRuntimeHost : ITenantRunControl
{
    private readonly HashSet<(Guid Tenant, Guid Run)> _cancelling = [];
    public async ValueTask<AgentRun> CancelAsync(TenantRunCancellation request, CancellationToken ct = default)
    {
        Live? live = null; var key = (request.Owner.TenantId ?? throw new AccessDeniedException("tenant_context_required"), request.RunId);
        lock (_gate)
        {
            if (_stopping) throw new AccessDeniedException("tenant_runtime_unavailable");
            if (_live.TryGetValue(request.SessionId, out var candidate) && candidate.Run.Owner == request.Owner
                && candidate.Run.AgentRunId == request.RunId && candidate.Run.Claim?.Generation == request.Generation) live = candidate;
            else if (!_cancelling.Add(key)) throw new SecurityConflictException("stale_revision");
        }
        if (live is not null)
        {
            var pending = StopLiveAsync(live, request);
            _ = ObserveCancellation(pending);
            return await pending.WaitAsync(ct).ConfigureAwait(false);
        }
        try
        {
            var saved = await cancellations.CommitAsync(request, ct).ConfigureAwait(false);
            Live? stopped = null;
            lock (_gate)
                if (_live.TryGetValue(request.SessionId, out var stale) && stale.Run.Owner == saved.Owner && stale.Run.AgentRunId == saved.AgentRunId)
                    stopped = stale;
            if (stopped is not null) StopTokens(stopped);
            return saved;
        }
        finally { lock (_gate) _cancelling.Remove(key); }
    }
    private async Task<AgentRun> StopLiveAsync(Live live, TenantRunCancellation request)
    {
        // Once admitted to the mailbox, finish the authorized command even if the HTTP client disconnects.
        var saved = await live.Runtime.CancelTenantRunAsync(request, cancellations).ConfigureAwait(false);
        bool current; lock (_gate) current = _live.GetValueOrDefault(request.SessionId) == live;
        if (current) StopTokens(live);
        return saved;
    }
    private void StopTokens(Live live)
    {
        // Cancellation callbacks belong to workers/providers and must never execute under the host registry lock.
        foreach (var source in new[] { live.OperationStop, live.Lifetime })
            try { source.Cancel(); }
            catch (ObjectDisposedException) { }
            catch (Exception ex) { DiagnosticLog.Warning(logger, ex, ids.NewId(), "Tenant cancellation callback failed."); }
    }
    private async Task ObserveCancellation(Task pending)
    {
        try { await pending.ConfigureAwait(false); }
        catch (Exception ex) when (ex is AccessDeniedException or SecurityConflictException) { }
        catch (Exception ex) { DiagnosticLog.Warning(logger, ex, ids.NewId(), "Tenant cancellation command failed."); }
    }
}
