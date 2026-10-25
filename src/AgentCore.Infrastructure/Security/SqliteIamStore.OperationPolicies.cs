using AgentCore.Application.Ports;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    public ValueTask<bool> TrySaveTenantOperationPolicyAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantSessionInitiator initiator, AuthorizationAudit audit, CancellationToken ct = default)
    {
        if (audit.ActorPrincipalId != initiator.PrincipalId || audit.Action is not ("tenant.operation_policy.update" or "tenant.chat_retention.update")) throw new ArgumentException("Invalid operation policy transaction.");
        return WriteTenantResourceAsync(next, expectedSecurityRevision, audit, async db =>
        { await RequireHumanSessionAsync(db, next.Tenant.TenantId, initiator, ct).ConfigureAwait(false); return true; }, ct);
    }
}
