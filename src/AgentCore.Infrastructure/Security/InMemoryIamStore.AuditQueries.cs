using AgentCore.Application.Ports;
using AgentCore.Application.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore : IAuthorizationAuditQueryStore
{
    public ValueTask<AuthorizationAuditPage> QueryAuditAsync(Guid? tenantId, AuthorizationAuditQuery query, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); query = AuthorizationAuditQueryRules.Normalize(query);
        lock (_gate)
        {
            var rows = AuthorizationAuditQueryRules.Filter(_audit.AsQueryable(), tenantId, query);
            var total = rows.Count();
            var items = rows.OrderByDescending(a => a.At).ThenByDescending(a => a.AuditId)
                .Skip(query.Page * query.PageSize).Take(query.PageSize).ToArray();
            return ValueTask.FromResult(new AuthorizationAuditPage(items, total, query.Page, query.PageSize));
        }
    }
}
