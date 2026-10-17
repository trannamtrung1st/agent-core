using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;

namespace AgentCore.Api.Realtime;

public sealed partial class TenantSessionRuntimeHost : ITenantDeliveryReceiptControl
{
    private readonly HashSet<Guid> _receipting = [];
    private long _receiptEpoch;

    public async ValueTask ReceiveAsync(TenantDeliveryReceipt proof, CancellationToken ct = default)
    {
        while (true)
        {
            Live? live;
            lock (_gate)
            {
                if (_stopping) throw new AccessDeniedException("tenant_runtime_unavailable");
                live = _live.GetValueOrDefault(proof.Owner.SessionId);
                if (live is null)
                {
                    if (!_receipting.Add(proof.Owner.SessionId)) throw new SecurityConflictException("stale_revision");
                    _receiptEpoch = checked(_receiptEpoch + 1);
                }
            }
            if (live is null)
            {
                try { await ((ITenantDeliveryReceiptStore)storage).SaveTenantQuiescentReceiptAsync(proof, ct).ConfigureAwait(false); return; }
                finally { lock (_gate) { _receipting.Remove(proof.Owner.SessionId); _receiptEpoch = checked(_receiptEpoch + 1); } }
            }
            if (live.Run.Owner.TenantId != proof.Owner.TenantId || live.Run.Owner.AgentInstanceId != proof.Owner.AgentInstanceId
                || live.Run.Owner.ExecutorPrincipalId != proof.Owner.ExecutorPrincipalId) throw new AccessDeniedException("receipt_unavailable");
            var current = await runs.FindTenantRunAsync(proof.Owner.TenantId, live.Run.AgentRunId, ct).ConfigureAwait(false);
            if (current is { Status: AgentRunStatus.Running, CancellationRequested: false, Claim: { } claim }
                && claim.Generation == live.Run.Claim!.Generation && claim.LeaseExpiresAtUtc > time.GetUtcNow())
            {
                try { if (await live.Runtime.ReceiveTenantDeliveryAsync(proof, ct).ConfigureAwait(false)) return; }
                catch (AccessDeniedException ex) when (ex.ReasonCode == "tenant_runtime_unavailable") { }
            }
            // A terminal writer may have won before this mailbox command. Wait for its exact owner
            // to retire before taking the quiescent reservation; never race it with a second writer.
            await CancelStaleLiveAsync(live, ct).ConfigureAwait(false);
            await live.Stopped.Task.WaitAsync(ct).ConfigureAwait(false);
        }
    }
}
