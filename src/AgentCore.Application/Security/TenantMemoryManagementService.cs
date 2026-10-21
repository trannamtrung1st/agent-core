using AgentCore.Application.Ports;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed class TenantMemoryManagementService(ITenantMemoryManagementStore memories, IIamStore security, IdentityService identity, IIdGenerator ids, TimeProvider time)
{
    public async ValueTask<TenantMemoryPage> ReadAsync(Guid tenant, Guid agent, HumanSessionReference human, TenantMemoryQuery query, CancellationToken ct = default)
    {
        TenantMemoryManagementRules.Validate(query);
        await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        var result = await memories.ReadOwnTenantMemoryAsync(tenant, agent, human, query, ct).ConfigureAwait(false);
        var current = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        if (current.Tenant.Revision != result.SecurityRevision) throw new SecurityConflictException("memory_changed");
        return result;
    }
    public async ValueTask<TenantMemoryForgetResult> ForgetAsync(Guid tenant, Guid agent, HumanSessionReference human, TenantMemoryForget change, CancellationToken ct = default)
    {
        await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        var audit = new AuthorizationAudit(ids.NewId(), tenant, human.PrincipalId, "memory.forget", agent.ToString("D"), "allowed", "human_memory_forgotten",
            DateTimeOffset.FromUnixTimeMilliseconds(time.GetUtcNow().ToUnixTimeMilliseconds()));
        TenantMemoryManagementRules.Validate(change, tenant, agent, human, audit);
        return await memories.TryForgetOwnTenantMemoryAsync(tenant, agent, human, change, audit, ct).ConfigureAwait(false)
            ?? throw new SecurityConflictException("stale_revision");
    }
}
