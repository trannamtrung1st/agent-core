using AgentCore.Application.Agents;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime
{
    private readonly TenantRunProfileService? _tenantProfiles;
    private async ValueTask<AgentDefinition> ProjectionDefinitionAsync(AgentDefinition admitted, AgentRun? run, Guid owner, CancellationToken ct)
    {
        if (_runtimeOwner is null) return await _tools.RestrictExecutionDefinitionAsync(admitted, owner, ct).ConfigureAwait(false);
        if (run is null || run.Owner != _runtimeOwner || _runAuthority is not AgentCore.Application.Ports.ITenantRunContext context)
            throw AgentCoreErrors.Forbidden("Tenant projection authority is unavailable.");
        var current = await context.CurrentProjectionDefinitionAsync(run, ct).ConfigureAwait(false)
            ?? throw AgentCoreErrors.Forbidden("Tenant execution authority is unavailable.");
        return StateIsolationAccess.RestrictDefinition(admitted, current);
    }
}
