using AgentCore.Domain.Security;

namespace AgentCore.Application.Ports;

public interface ITenantAuthorizationAuditStore
{
    // Validates actual persisted Run lineage and observed revisions, then appends only evidence.
    // False fences a stale decision; it never changes Run/security state or grants authority.
    ValueTask<bool> TryRecordOperationAuthorizationAsync(AuthorizationAudit audit, CancellationToken ct = default);
}
