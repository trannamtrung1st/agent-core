using AgentCore.Domain.Security;

namespace AgentCore.Application.Ports;

public sealed record TenantOperationCommit(Guid RequestId, Guid OperationId, Guid RunId, Guid Generation,
    long ExpectedRunRevision, long ExpectedRequestRevision, Guid ExpectedDecisionId, long? ExpectedGrantRevision,
    string ActionHash, TenantOperationOutcome Outcome, string ReasonCode);

public interface ITenantApprovedOperationStore
{
    ValueTask<TenantApprovalDecision?> FindApprovalDecisionAsync(Guid tenantId, Guid requestId, CancellationToken ct = default);
    ValueTask<TenantOperationReceipt?> FindOperationReceiptAsync(Guid tenantId, Guid operationId, CancellationToken ct = default);
    ValueTask<TenantOperationReceipt?> TryCommitSandboxControlAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantOperationCommit commit, AuthorizationAudit audit, CancellationToken ct = default) => throw new NotSupportedException();
    // Existing Instance, exact receipt, grant consume/revoke, Run effect checkpoint, security revision and audit are one transaction.
    ValueTask<TenantOperationReceipt?> TryCommitLocalEffectAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantOperationCommit commit, AuthorizationAudit audit, CancellationToken ct = default);
}
