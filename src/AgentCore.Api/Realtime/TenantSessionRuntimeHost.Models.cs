using AgentCore.Application.Ports;
using AgentCore.Application.Security;

namespace AgentCore.Api.Realtime;

public sealed partial class TenantSessionRuntimeHost
{
    public async ValueTask<TenantSession> SelectModelAsync(TenantSessionService service, Guid tenant, Guid session,
        TenantSessionInitiator human, long securityRevision, long sessionRevision, string? offeringId, string? reasoningEffort, CancellationToken ct)
    {
        // Authorize the resource before reserving it. The mutation repeats all human, Agent,
        // configuration and revision checks. Active work is never rewritten or interrupted.
        _ = await service.ReadAsync(tenant, session, human, ct).ConfigureAwait(false);
        lock (_gate)
        {
            if (_stopping) throw new AccessDeniedException("tenant_runtime_unavailable");
            if (_live.ContainsKey(session) || _voices.ContainsKey(session) || !_quiescentWrites.Add(session))
                throw new SecurityConflictException("session_in_use");
            _sessionWriteEpoch = checked(_sessionWriteEpoch + 1);
        }
        try { return await service.SelectModelAsync(tenant, session, human, securityRevision, sessionRevision, offeringId, reasoningEffort, ct).ConfigureAwait(false); }
        finally { lock (_gate) { _quiescentWrites.Remove(session); _sessionWriteEpoch = checked(_sessionWriteEpoch + 1); } }
    }
}
