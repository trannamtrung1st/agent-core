using AgentCore.Application.Ports;
using AgentCore.Domain.Security;
namespace AgentCore.Application.Security;
public sealed partial class TenantApprovalService
{
    public async ValueTask<TenantManagementApprovalVisibility> ManagementVisibilityAsync(Guid tenant, TenantSessionInitiator human, CancellationToken ct = default)
    {
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        return await ManagementVisibility(state, human.PrincipalId, ct).ConfigureAwait(false);
    }
    private async ValueTask<TenantManagementApprovalVisibility> ManagementVisibility(TenantSecurityState state, Guid human, CancellationToken ct) => new(
        await VisibleIds(state, human, "agents.read", "agent", ct).ConfigureAwait(false),
        new(await VisibleIds(state, human, "skills.read", "agent", ct).ConfigureAwait(false), await VisibleResourceReadScopes(state, human, ct, "skills.read", "skill").ConfigureAwait(false)),
        new(await VisibleIds(state, human, "resources.read", "agent", ct).ConfigureAwait(false), await VisibleResourceReadScopes(state, human, ct).ConfigureAwait(false)),
        await VisibleIds(state, human, "credentials.read", "credential", ct).ConfigureAwait(false));
}
