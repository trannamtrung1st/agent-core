using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;

namespace AgentCore.Api.Realtime;

public sealed partial class TenantSessionRuntimeHost : ITenantSessionContextControl
{
    public async ValueTask<TenantContextSwitchResult> SwitchContextAsync(TenantContextSwitch request, CancellationToken ct = default)
    {
        while (true)
        {
            Live? live;
            lock (_gate)
            {
                if (_stopping) throw new AccessDeniedException("tenant_runtime_unavailable");
                live = _live.GetValueOrDefault(request.Owner.SessionId);
                if (live is null)
                {
                    if (!_quiescentWrites.Add(request.Owner.SessionId)) throw new SecurityConflictException("session_in_use");
                    _sessionWriteEpoch = checked(_sessionWriteEpoch + 1);
                }
            }
            if (live is null)
            {
                try { return await ((ITenantSessionContextStore)storage).SwitchTenantQuiescentContextAsync(request, ids.NewId(), ct).ConfigureAwait(false); }
                finally { lock (_gate) { _quiescentWrites.Remove(request.Owner.SessionId); _sessionWriteEpoch = checked(_sessionWriteEpoch + 1); } }
            }
            if (live.Run.Owner != AgentRunOwner.ForTenant(request.Owner.TenantId, request.Owner.AgentInstanceId, request.Owner.ExecutorPrincipalId)) throw new AccessDeniedException("tenant_context_mismatch");
            var current = await runs.FindTenantRunAsync(request.Owner.TenantId, live.Run.AgentRunId, ct).ConfigureAwait(false);
            if (current is { Status: AgentRunStatus.Running, CancellationRequested: false, Claim: { } claim }
                && claim.Generation == live.Run.Claim!.Generation && claim.LeaseExpiresAtUtc > time.GetUtcNow())
            {
                ct.ThrowIfCancellationRequested(); var pending = live.Runtime.SwitchTenantContextAsync(request); _ = ObserveInputAsync(pending);
                if (await pending.WaitAsync(ct).ConfigureAwait(false) is { } result) return result;
                throw new SecurityConflictException("session_in_use");
            }
            await CancelStaleLiveAsync(live, ct).ConfigureAwait(false); await live.Stopped.Task.WaitAsync(ct).ConfigureAwait(false);
        }
    }
}
