using AgentCore.Application.Ports;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

internal static class TenantCredentialOAuthAuthority
{
    internal static async ValueTask<TenantSecurityState> RequireAsync(IIamStore security, IdentityService identity, IamAuthorizer authorizer,
        Guid tenant, Guid credential, CredentialOAuthInitiator initiator, bool use, CancellationToken ct)
    {
        TenantSecurityState state;
        try { state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, initiator, ct).ConfigureAwait(false); }
        catch (AccessDeniedException ex) when (ex.ReasonCode == "initiator_unavailable") { throw new AccessDeniedException("oauth_initiator_unavailable"); }
        foreach (var key in use ? new[] { "credentials.manage", "credentials.read", "credentials.use" } : new[] { "credentials.manage", "credentials.read" })
        {
            var decision = await authorizer.CheckTenantAsync(state, initiator.PrincipalId, PermissionCatalog.Get(key), TenantCredentialRules.Scope(tenant, credential), ct).ConfigureAwait(false);
            if (!decision.Allowed) throw new AccessDeniedException(decision.ReasonCode);
        }
        return state;
    }
}
