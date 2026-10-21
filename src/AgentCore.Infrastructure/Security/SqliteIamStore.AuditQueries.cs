using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore : IAuthorizationAuditQueryStore
{
    public async ValueTask<AuthorizationAuditPage> QueryAuditAsync(Guid? tenantId, AuthorizationAuditQuery query, CancellationToken ct = default)
    {
        query = AuthorizationAuditQueryRules.Normalize(query);
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var rows = AuthorizationAuditQueryRules.Filter(db.Set<AuthorizationAudit>().AsNoTracking(), tenantId, query);
        var total = await rows.CountAsync(ct).ConfigureAwait(false);
        var items = await rows.OrderByDescending(a => a.At).ThenByDescending(a => a.AuditId)
            .Skip(query.Page * query.PageSize).Take(query.PageSize).ToArrayAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return new(items, total, query.Page, query.PageSize);
    }
}
