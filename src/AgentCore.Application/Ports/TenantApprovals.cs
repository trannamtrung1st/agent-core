using AgentCore.Domain.Security;

namespace AgentCore.Application.Ports;

public sealed record TenantApprovalContinuationIds(Guid ActivationId, Guid RunId);
public sealed record TenantApprovalQuery(int Page = 0, int PageSize = 20, TenantApprovalStatus? Status = null, ApprovalKind? Kind = null)
{
    public void Validate()
    {
        if (Page is < 0 or > 100000 || PageSize is < 1 or > 100 || Status is { } status && !Enum.IsDefined(status)
            || Kind is { } kind && !Enum.IsDefined(kind)) throw new ArgumentException("Invalid approval inventory query.");
    }
}
public sealed record TenantApprovalPage(IReadOnlyList<TenantApprovalRequest> Items, int Total, int Page, int PageSize);

public interface ITenantApprovalStore
{
    ValueTask<TenantApprovalRequest?> FindApprovalAsync(Guid tenantId, Guid requestId, CancellationToken ct = default);
    ValueTask<TenantApprovalRequest?> FindRootApprovalAsync(Guid tenantId, Guid rootId, Guid executorId, PreparedTenantOperation operation, CancellationToken ct = default);
    ValueTask<TenantApprovalPage> ListApprovalsAsync(Guid tenantId, IReadOnlyList<Guid>? allowedRequests,
        IReadOnlyList<Guid>? allowedInstances, TenantApprovalQuery query, CancellationToken ct = default);
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
