using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Security;

// A trusted source-preparation boundary, distinct from reusable configuration and execution.
public sealed class TenantSandboxImageAuthorization(IIamStore security, ITenantSandboxProfileStore profiles,
    IdentityService identity, IamAuthorizer authorizer)
{
    public async ValueTask RequireAsync(SandboxProfile profile, SandboxImageAuthorization proof, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(proof);
        if (profile.TenantId is not { } tenant || tenant == Guid.Empty) throw new AccessDeniedException("tenant_scope_required");
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, proof.Human, ct).ConfigureAwait(false);
        var existing = await profiles.FindTenantSandboxProfileAsync(tenant, profile.Id, ct: ct).ConfigureAwait(false);
        var target = existing is null ? AgentCore.Domain.Security.AccessScope.ForTenant(tenant) : TenantSandboxProfileRules.Scope(tenant, profile.Id);
        foreach (var key in new[] { "sandbox.read", "sandbox.manage" })
            await authorizer.RequireAsync(proof.Human.PrincipalId, key, target, ct).ConfigureAwait(false);
        if (profile.RegistryCredentialId is { } credential)
            foreach (var key in new[] { "credentials.read", "credentials.bind", "credentials.use" })
                await authorizer.RequireAsync(proof.Human.PrincipalId, key, TenantCredentialRules.Scope(tenant, credential), ct).ConfigureAwait(false);
        var current = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, proof.Human, ct).ConfigureAwait(false);
        if (state.Tenant.Revision != proof.SecurityRevision || current.Tenant.Revision != proof.SecurityRevision)
            throw new SecurityConflictException("stale_revision");
    }
}
