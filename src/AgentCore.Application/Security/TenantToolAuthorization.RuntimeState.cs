using AgentCore.Application.Admin;
using AgentCore.Application.Ports;

namespace AgentCore.Application.Security;

public sealed partial class TenantToolAuthorization
{
    internal async ValueTask<RuntimeStateObservation?> RuntimeStateFactsAsync(Guid session, Guid? runId, Guid? generation, CancellationToken ct)
    {
        if (runId is not { } id || generation is not { } claim || runtimeState is null) return null;
        var owner = await runs.ResolveTenantSessionOwnerAsync(session, ct).ConfigureAwait(false);
        if (owner is null) return null;
        var run = await runs.FindTenantRunAsync(owner.TenantId, id, ct).ConfigureAwait(false);
        if (run is null || run.SessionId != session) return null;
        return await runtimeState().ObserveForRunAsync(new(run.Owner, session, id, claim), rateLimit: false, ct: ct).ConfigureAwait(false);
    }
}
