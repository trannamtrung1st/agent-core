using AgentCore.Domain.Security;
using Microsoft.EntityFrameworkCore;
namespace AgentCore.Infrastructure.Security;
public sealed partial class SqliteIamStore
{
    public async ValueTask<IReadOnlyList<Guid>> ListAssistantTenantPageAsync(int page, int limit, CancellationToken ct = default)
    {
        if (page < 0 || limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(page));
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.Set<Tenant>().AsNoTracking().Where(t => t.Status == IdentityStatus.Active).OrderBy(t => t.TenantId)
            .Skip(checked(page * limit)).Take(limit).Select(t => t.TenantId).ToArrayAsync(ct);
    }
    public async ValueTask<AuthorizationAudit?> FindAgentOperationAsync(Guid tenant, Guid operation, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.Set<AuthorizationAudit>().AsNoTracking().SingleOrDefaultAsync(a => a.TenantId == tenant && a.AuditId == operation, ct);
    }
}
