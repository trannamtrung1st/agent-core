using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Security;

// A trusted source-preparation boundary, distinct from reusable configuration and execution.
public sealed class TenantSandboxImageAuthorization(IIamStore security, ITenantSandboxProfileStore profiles,
    IdentityService identity, IamAuthorizer authorizer, TenantSandboxSetupResolver? setups = null)
{
    public async ValueTask RequireAsync(SandboxProfile profile, SandboxImageAuthorization proof, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(proof);
        if (profile.TenantId is not { } tenant || tenant == Guid.Empty) throw new AccessDeniedException("tenant_scope_required");
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, proof.Human, ct).ConfigureAwait(false);
        var existing = await profiles.FindTenantSandboxProfileAsync(tenant, profile.Id, ct: ct).ConfigureAwait(false);
        if (proof.ExpectedProfileRevision is { } revision && (existing?.Revision ?? 0) != revision) throw new SecurityConflictException("stale_revision");
        var target = existing is null ? AgentCore.Domain.Security.AccessScope.ForTenant(tenant) : TenantSandboxProfileRules.Scope(tenant, profile.Id);
        if (proof.InstanceId is null && proof.ExpectedInstanceRevision is not null || proof.InstanceId is not null && proof.ExpectedInstanceRevision is not > 0)
            throw new AccessDeniedException("tenant_scope_required");
        foreach (var key in proof.InstanceId is null ? new[] { "sandbox.read", "sandbox.manage" } : new[] { "sandbox.read" })
            await authorizer.RequireAsync(proof.Human.PrincipalId, key, target, ct).ConfigureAwait(false);
        if (proof.InstanceId is { } instance)
        {
            if (existing is null || setups is null) throw new AccessDeniedException("resource_unavailable");
            if (!state.SystemIdentities.Any(i => i.AgentInstanceId == instance && i.Status == AgentCore.Domain.Security.IdentityStatus.Active))
                throw new AccessDeniedException("resource_unavailable");
            var scope = AgentCore.Domain.Security.AccessScope.ForResource(tenant, "agent", instance.ToString("D"));
            foreach (var key in new[] { "agents.read", "sandbox.read", "sandbox.manage" })
                await authorizer.RequireAsync(proof.Human.PrincipalId, key, scope, ct).ConfigureAwait(false);
            var setup = await setups.ResolveAsync(tenant, instance, profile.Id, ct).ConfigureAwait(false);
            if (setup.Agent.Instance.Revision != proof.ExpectedInstanceRevision) throw new SecurityConflictException("stale_revision");
            TenantSandboxVerificationRules.RequireCurrent(profile, setup.Profile);
        }
        if (profile.RegistryCredentialId is { } credential)
            foreach (var key in new[] { "credentials.read", "credentials.bind", "credentials.use" })
                await authorizer.RequireAsync(proof.Human.PrincipalId, key, TenantCredentialRules.Scope(tenant, credential), ct).ConfigureAwait(false);
        var current = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, proof.Human, ct).ConfigureAwait(false);
        if (state.Tenant.Revision != proof.SecurityRevision || current.Tenant.Revision != proof.SecurityRevision)
            throw new SecurityConflictException("stale_revision");
    }
}
