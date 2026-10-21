using AgentCore.Application.Ports;
using AgentCore.Application.Security;

namespace AgentCore.Api.Realtime;

public sealed partial class TenantSessionRuntimeHost : ITenantRequestIsolationControl
{
    public async ValueTask<bool> SettleRequestAsync(TenantSessionRoot owner, CancellationToken ct = default)
    {
        // No-op scans do not reserve a Session or disturb admission. This read grants
        // no write: the service and owning transaction repeat the proof under reservation.
        if (requestIsolationStore is null || (await requestIsolationStore.ReadTenantRequestIsolationAsync(owner, ct).ConfigureAwait(false))?.Ready != true) return false;
        lock (_gate)
        {
            if (_stopping || _live.ContainsKey(owner.SessionId) || _voices.ContainsKey(owner.SessionId) || !_quiescentWrites.Add(owner.SessionId)) return false;
        }
        try
        {
            var settled = requestIsolation is not null && await requestIsolation().SettleQuiescentAsync(owner, ct).ConfigureAwait(false);
            if (settled)
            {
                lock (_gate) foreach (var pending in _dispatching.Values.Where(p => p.Session == owner.SessionId)) pending.RequestSettled = true;
                await delivery.PublishSettledRequestAsync(owner, ct).ConfigureAwait(false);
            }
            return settled;
        }
        finally { lock (_gate) _quiescentWrites.Remove(owner.SessionId); }
    }
    private async ValueTask SettleReleasedRequestAsync(Live live)
    {
        try
        {
            var owner = await inputSessions.FindTenantSessionAsync(live.Run.Owner.TenantId!.Value, live.Run.SessionId).ConfigureAwait(false);
            if (owner is not null) await SettleRequestAsync(owner.Owner).ConfigureAwait(false);
            // A child's completion can unblock its parent's already settled request.
            if (owner?.Snapshot.Origin.OriginatingSessionId is { } parentId
                && await inputSessions.FindTenantSessionAsync(owner.Owner.TenantId, parentId).ConfigureAwait(false) is { } parent)
                await SettleRequestAsync(parent.Owner).ConfigureAwait(false);
        }
        catch (Exception ex) { AgentCore.Application.Observability.DiagnosticLog.Warning(logger, ex, ids.NewId(), "Tenant request settlement deferred.", new(SessionId: live.Run.SessionId)); }
    }
}
