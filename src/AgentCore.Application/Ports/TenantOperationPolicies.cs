using AgentCore.Domain.Security;

namespace AgentCore.Application.Ports;

public interface ITenantOperationPolicyStore
{
    ValueTask<bool> TrySaveTenantOperationPolicyAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantSessionInitiator initiator, AuthorizationAudit audit, CancellationToken ct = default);
}
