using AgentCore.Domain.Security;

namespace AgentCore.Application.Ports;

public sealed record TenantApprovalContinuationIds(Guid ActivationId, Guid RunId);

public interface ITenantApprovalStore
{
    ValueTask<TenantApprovalRequest?> FindApprovalAsync(Guid tenantId, Guid requestId, CancellationToken ct = default);
    ValueTask<TenantApprovalRequest?> FindRootApprovalAsync(Guid tenantId, Guid rootId, Guid executorId, PreparedTenantOperation operation, CancellationToken ct = default);
    ValueTask<IReadOnlyList<TenantApprovalRequest>> ListApprovalsAsync(Guid tenantId, IReadOnlyList<Guid>? allowedRequests,
        IReadOnlyList<Guid>? allowedInstances, int limit, CancellationToken ct = default);
    ValueTask<TenantApprovalRequest?> TryCreateApprovalAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantApprovalRequest request, long expectedRunRevision, Guid? generation, AuthorizationAudit audit, CancellationToken ct = default);
    ValueTask<bool> TryDecideApprovalAsync(TenantSecurityState next, long expectedSecurityRevision, TenantApprovalRequest request,
        long expectedRequestRevision, TenantApprovalDecision decision, TenantOperationGrant? grant, ApprovalProcessedSignal signal,
        TenantSessionInitiator human, AuthorizationAudit audit, CancellationToken ct = default);
    ValueTask<IReadOnlyList<ApprovalProcessedSignal>> PendingApprovalSignalsAsync(int limit, CancellationToken ct = default);
    ValueTask<IReadOnlyList<TenantApprovalRequest>> DueApprovalsAsync(DateTimeOffset at, int limit, CancellationToken ct = default);
    ValueTask<bool> TryExpireApprovalAsync(Guid tenantId, Guid requestId, long expectedRevision, ApprovalProcessedSignal signal, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<bool> TryResumeApprovalAsync(ApprovalProcessedSignal signal, DateTimeOffset at, CancellationToken ct = default, TenantApprovalContinuationIds? continuation = null);
    ValueTask<TenantOperationGrant?> FindOperationGrantAsync(Guid tenantId, Guid requestId, CancellationToken ct = default);
}
