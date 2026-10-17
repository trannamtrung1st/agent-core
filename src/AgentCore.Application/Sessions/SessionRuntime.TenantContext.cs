using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime
{
    private async ValueTask<TenantRequesterContext?> RequesterContextAsync(AgentRun? run, CancellationToken ct)
    {
        if (_runtimeOwner is null) return null;
        if (run is null || run.Owner != _runtimeOwner || _runAuthority is not ITenantRunContext source)
            throw AgentCoreErrors.Forbidden("Tenant requester context is unavailable.");
        return await source.CurrentRequesterAsync(run, ct).ConfigureAwait(false);
    }
}
