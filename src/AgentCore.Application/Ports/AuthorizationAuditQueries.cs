using AgentCore.Domain.Security;

namespace AgentCore.Application.Ports;

public sealed record AuthorizationAuditQuery(int Page = 0, int PageSize = 30, string? Action = null,
    string? Decision = null, Guid? ActorPrincipalId = null, DateTimeOffset? From = null, DateTimeOffset? Through = null);
public sealed record AuthorizationAuditPage(IReadOnlyList<AuthorizationAudit> Items, int Total, int Page, int PageSize);

// Read facet of the existing canonical IAM/audit owner. Null tenant selects only
// the closed body-free System facts; it never means all organizations.
public interface IAuthorizationAuditQueryStore
{
    ValueTask<AuthorizationAuditPage> QueryAuditAsync(Guid? tenantId, AuthorizationAuditQuery query, CancellationToken ct = default);
}
