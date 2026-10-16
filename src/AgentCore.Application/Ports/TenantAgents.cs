using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Ports;

public sealed record TenantAgent(Guid TenantId, Guid SystemIdentityId, Guid PrincipalId, AgentInstance Instance);
public sealed record TenantAgentPage(IReadOnlyList<TenantAgent> Items, int Total, int Page, int PageSize);

// Specialized transaction boundary: the existing Instance, its identity, security revision and audit
// are committed together. Reads filter authoritative identity ownership before ordering/pagination.
public interface ITenantAgentStore
{
    ValueTask<TenantAgent?> FindTenantAgentAsync(Guid tenantId, Guid instanceId, CancellationToken ct = default);
    ValueTask<TenantAgentPage> ListTenantAgentsAsync(Guid tenantId, IReadOnlyList<Guid>? allowedInstanceIds,
        int page, int pageSize, CancellationToken ct = default);
    ValueTask<bool> TryCreateTenantAgentAsync(TenantSecurityState next, long expectedSecurityRevision,
        AgentInstance instance, IReadOnlyList<SkillSpec> skills, AuthorizationAudit audit, CancellationToken ct = default);
    ValueTask<TenantAgent?> TrySetTenantAgentLifecycleAsync(TenantSecurityState next, long expectedSecurityRevision,
        Guid instanceId, long expectedInstanceRevision, AgentInstanceLifecycle lifecycle, DateTimeOffset at,
        AuthorizationAudit audit, CancellationToken ct = default);
}
