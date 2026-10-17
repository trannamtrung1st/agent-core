using AgentCore.Domain.Security;

namespace AgentCore.Application.Ports;

public sealed record TenantCredentialCheckStart(TenantCredentialCheckDispatch? Dispatch, TenantOperationReceipt? Receipt);

public interface ITenantCredentialCheckStore
{
    ValueTask<TenantCredentialCheckDispatch?> FindCredentialCheckDispatchAsync(Guid tenantId, Guid operationId, CancellationToken ct = default);
    ValueTask<IReadOnlyList<TenantCredentialCheckDispatch>> RecoverableCredentialChecksAsync(DateTimeOffset at, int limit, CancellationToken ct = default);
    // Only a newly committed fence is returned as Dispatch. A duplicate can never acquire provider authority.
    ValueTask<TenantCredentialCheckStart?> TryStartCredentialCheckAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantOperationCommit commit, AuthorizationAudit audit, CancellationToken ct = default);
    // A factual completion of the already-dispatched action, not fresh effect authority. Null check means unknown outcome.
    ValueTask<TenantOperationReceipt?> TrySettleCredentialCheckAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantCredentialCheckDispatch dispatch, CredentialOAuthCheck? check, AuthorizationAudit audit, bool recovery = false, CancellationToken ct = default);
}
