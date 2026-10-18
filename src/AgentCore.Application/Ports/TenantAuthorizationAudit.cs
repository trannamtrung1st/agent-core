using AgentCore.Domain.Security;

namespace AgentCore.Application.Ports;

public interface ITenantAuthorizationAuditStore
{
    // Validates actual persisted Run lineage and observed revisions, then appends only evidence.
    // False fences a stale decision; it never changes Run/security state or grants authority.
    ValueTask<bool> TryRecordOperationAuthorizationAsync(AuthorizationAudit audit, CancellationToken ct = default);
    // Repeats actual original-human/owner/revision/IAM checks immediately before a filesystem commit.
    // Evidence only: it is neither a mutation receipt nor permission for a subsequent call.
    ValueTask<bool> TryRecordWorkspaceAuthorizationAsync(TenantWorkspaceHumanAuthorization proof, CancellationToken ct = default);
}
