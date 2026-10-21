using AgentCore.Application.Ports;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;
public sealed class TenantExperienceManagementService(ITenantExperienceManagementStore experiences, IIamStore security, IdentityService identity, IIdGenerator ids, TimeProvider time)
{
    public async ValueTask<TenantExperiencePage> ReadAsync(Guid tenant, Guid agent, HumanSessionReference human, TenantExperienceQuery query, CancellationToken ct = default)
    {
        TenantExperienceManagementRules.Validate(query);
        await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        var result = await experiences.ReadOwnTenantExperienceAsync(tenant, agent, human, query, ct).ConfigureAwait(false);
        var current = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        if (current.Tenant.Revision != result.SecurityRevision) throw new SecurityConflictException("experience_changed");
        return result;
    }
    public async ValueTask<TenantExperienceChangeResult> ChangeAsync(Guid tenant, Guid agent, HumanSessionReference human, TenantExperienceChange change, CancellationToken ct = default)
    {
        await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        var audit = new AuthorizationAudit(ids.NewId(), tenant, human.PrincipalId, "experience.manage", agent.ToString("D"), "allowed", "human_experience_changed",
            DateTimeOffset.FromUnixTimeMilliseconds(time.GetUtcNow().ToUnixTimeMilliseconds()));
        TenantExperienceManagementRules.Validate(change, tenant, agent, human, audit);
        return await experiences.TryChangeOwnTenantExperienceAsync(tenant, agent, human, change, audit, ct).ConfigureAwait(false)
            ?? throw new SecurityConflictException("stale_revision");
    }
}
