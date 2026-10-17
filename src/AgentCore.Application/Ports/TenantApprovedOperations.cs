using AgentCore.Domain.Security;

namespace AgentCore.Application.Ports;

public sealed record TenantPersonaEffectCommit(Guid RequestId, Guid OperationId, Guid RunId, Guid Generation,
    long ExpectedRunRevision, long ExpectedRequestRevision, Guid ExpectedDecisionId, long? ExpectedGrantRevision,
    string ActionHash, TenantOperationOutcome Outcome, string ReasonCode);

public interface ITenantApprovedOperationStore
{
    ValueTask<TenantApprovalDecision?> FindApprovalDecisionAsync(Guid tenantId, Guid requestId, CancellationToken ct = default);
    ValueTask<TenantOperationReceipt?> FindOperationReceiptAsync(Guid tenantId, Guid operationId, CancellationToken ct = default);
    // Existing Instance, exact receipt, grant consume/revoke, Run effect checkpoint, security revision and audit are one transaction.
    ValueTask<TenantOperationReceipt?> TryCommitPersonaEffectAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantPersonaEffectCommit commit, AuthorizationAudit audit, CancellationToken ct = default);
}
