using AgentCore.Domain.Security;

namespace AgentCore.Application.Ports;

public sealed record TenantOperationCommit(Guid RequestId, Guid OperationId, Guid RunId, Guid Generation,
    long ExpectedRunRevision, long ExpectedRequestRevision, Guid ExpectedDecisionId, long? ExpectedGrantRevision,
    string ActionHash, TenantOperationOutcome Outcome, string ReasonCode);

public sealed record TenantBrowserEffectFence(Guid TenantId, Guid RequestId, Guid OperationId, Guid RunId,
    string ActionHash, AgentCore.Domain.Conversation.AgentRunExternalEffect Origin);
public sealed record TenantBrowserEffectObservation(long RunRevision, string Json);
public sealed record TenantBrowserEffectStart(TenantBrowserEffectFence? Fence, TenantOperationReceipt? Receipt);

public interface ITenantDirectOperationStore
{
    ValueTask<TenantOperationReceipt?> FindDirectOperationReceiptAsync(Guid tenantId, Guid runId, string sourceCallId, CancellationToken ct = default);
}

public interface ITenantApprovedOperationStore
{
    ValueTask<TenantApprovalDecision?> FindApprovalDecisionAsync(Guid tenantId, Guid requestId, CancellationToken ct = default);
    ValueTask<TenantOperationReceipt?> FindOperationReceiptAsync(Guid tenantId, Guid operationId, CancellationToken ct = default);
    // Core local configuration/control/read consent, exact receipt, grant consume/revoke, Run effect checkpoint, security revision and audit are one transaction.
    ValueTask<TenantOperationReceipt?> TryCommitLocalEffectAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantOperationCommit commit, AuthorizationAudit audit, CancellationToken ct = default);
    ValueTask<TenantBrowserEffectStart?> TryStartBrowserEffectAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantOperationCommit commit, AuthorizationAudit audit, CancellationToken ct = default);
    ValueTask<IReadOnlyList<TenantBrowserEffectFence>> RecoverableBrowserEffectsAsync(DateTimeOffset at, int limit, CancellationToken ct = default);
    ValueTask<TenantOperationReceipt?> TrySettleBrowserEffectAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantBrowserEffectFence fence, TenantOperationOutcome outcome, string reason, AuthorizationAudit audit,
        bool recovery = false, CancellationToken ct = default, TenantBrowserEffectObservation? observation = null);
}
