using AgentCore.Application.Ports;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

internal static class TenantHumanSessionAuthority
{
    internal static async ValueTask<TenantSecurityState> RequireAsync(IIamStore security, IdentityService identity, Guid tenant, HumanSessionReference initiator, CancellationToken ct)
    {
        var account = await identity.ValidateSessionAsync(initiator.AuthenticationSessionId, ct).ConfigureAwait(false);
        var user = await identity.ResolveUserAsync(initiator.AccountId, tenant, ct).ConfigureAwait(false);
        if (account?.AccountId != initiator.AccountId || user?.PrincipalId != initiator.PrincipalId) throw new AccessDeniedException("initiator_unavailable");
        return await security.ReadTenantAsync(tenant, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
    }
}
