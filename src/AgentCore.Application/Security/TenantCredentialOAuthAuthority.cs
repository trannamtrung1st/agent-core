using AgentCore.Application.Ports;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

internal static class TenantCredentialOAuthAuthority
{
    internal static async ValueTask<TenantSecurityState> RequireAsync(IIamStore security, IdentityService identity, IamAuthorizer authorizer,
        Guid tenant, Guid credential, CredentialOAuthInitiator initiator, bool use, CancellationToken ct)
    {
        var account = await identity.ValidateSessionAsync(initiator.AuthenticationSessionId, ct).ConfigureAwait(false);
        var user = await identity.ResolveUserAsync(initiator.AccountId, tenant, ct).ConfigureAwait(false);
        if (account?.AccountId != initiator.AccountId || user?.PrincipalId != initiator.PrincipalId) throw new AccessDeniedException("oauth_initiator_unavailable");
        var state = await security.ReadTenantAsync(tenant, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        foreach (var key in use ? new[] { "credentials.manage", "credentials.read", "credentials.use" } : new[] { "credentials.manage", "credentials.read" })
        {
            var decision = await authorizer.CheckTenantAsync(state, initiator.PrincipalId, PermissionCatalog.Get(key), TenantCredentialRules.Scope(tenant, credential), ct).ConfigureAwait(false);
            if (!decision.Allowed) throw new AccessDeniedException(decision.ReasonCode);
        }
        return state;
    }
}
