using AgentCore.Domain.Security;

namespace AgentCore.Application.Ports;

public sealed record TenantSandboxApprovalVisibility(IReadOnlyList<Guid>? Instances, IReadOnlyList<string>? Profiles, IReadOnlyList<Guid>? Sessions, IReadOnlyList<Guid>? ControlInstances = null, IReadOnlyList<Guid>? ReadInstances = null);
public sealed record TenantBrowserApprovalVisibility(IReadOnlyList<Guid>? ReadInstances, IReadOnlyList<Guid>? UseInstances, IReadOnlyList<Guid>? Sessions);
public sealed record TenantArtifactReadApprovalVisibility(IReadOnlyList<Guid>? Artifacts, IReadOnlyList<Guid>? Sessions);
public sealed record TenantCredentialReadApprovalVisibility(IReadOnlyList<Guid>? Credentials, IReadOnlyList<Guid>? Sessions);
public sealed record TenantApprovalContinuationIds(Guid ActivationId, Guid RunId);
public sealed record TenantApprovalQuery(int Page = 0, int PageSize = 20, TenantApprovalStatus? Status = null, ApprovalKind? Kind = null, Guid? SessionId = null)
{
    public void Validate()
    {
        if (Page is < 0 or > 100000 || PageSize is < 1 or > 100 || Status is { } status && !Enum.IsDefined(status)
            || Kind is { } kind && !Enum.IsDefined(kind) || SessionId == Guid.Empty) throw new ArgumentException("Invalid approval inventory query.");
    }
}
public sealed record TenantApprovalPage(IReadOnlyList<TenantApprovalRequest> Items, int Total, int Page, int PageSize);

public interface ITenantApprovalStore
{
    ValueTask<TenantApprovalRequest?> FindApprovalAsync(Guid tenantId, Guid requestId, CancellationToken ct = default);
    ValueTask<TenantApprovalRequest?> FindRootApprovalAsync(Guid tenantId, Guid rootId, Guid executorId, PreparedTenantOperation operation, CancellationToken ct = default);
    ValueTask<TenantApprovalPage> ListApprovalsAsync(Guid tenantId, IReadOnlyList<Guid>? allowedRequests,
        IReadOnlyList<Guid>? allowedInstances, TenantApprovalQuery query, CancellationToken ct = default, IReadOnlyList<Guid>? allowedCredentials = null, TenantSandboxApprovalVisibility? sandboxVisibility = null, TenantBrowserApprovalVisibility? browserVisibility = null, TenantCredentialReadApprovalVisibility? credentialReadVisibility = null, IReadOnlyList<Guid>? artifactCreateSessions = null, TenantArtifactReadApprovalVisibility? artifactReadVisibility = null);
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
