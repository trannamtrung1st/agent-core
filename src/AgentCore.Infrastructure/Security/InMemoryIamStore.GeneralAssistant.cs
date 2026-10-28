using AgentCore.Application.Security;
using AgentCore.Domain.Security;
namespace AgentCore.Infrastructure.Security;
public sealed partial class InMemoryIamStore
{
    public ValueTask<IReadOnlyList<Guid>> ListAssistantTenantPageAsync(int page, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (page < 0 || limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(page));
        lock (_gate) return ValueTask.FromResult<IReadOnlyList<Guid>>(_tenants.Values.Where(s => s.Tenant.Status == IdentityStatus.Active)
            .OrderBy(s => s.Tenant.TenantId).Skip(checked(page * limit)).Take(limit).Select(s => s.Tenant.TenantId).ToArray());
    }
    public ValueTask<AuthorizationAudit?> FindAgentOperationAsync(Guid tenant, Guid operation, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate) return ValueTask.FromResult(_audit.SingleOrDefault(a => a.TenantId == tenant && a.AuditId == operation));
    }
}
