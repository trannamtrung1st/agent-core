using AgentCore.Domain.Security;

namespace AgentCore.Application.Ports;

public sealed record TenantPreferenceCommit(Guid OperationId, Guid RunId, Guid Generation, long ExpectedRunRevision,
    string SourceCallId, string ArgumentsJson, string ParametersHash, long ExpectedInstanceRevision, string ActionHash);

public interface ITenantPreferenceOperationStore
{
    ValueTask<TenantOperationReceipt?> FindPreferenceReceiptAsync(Guid tenantId, Guid runId, string sourceCallId, CancellationToken ct = default);
    ValueTask<TenantOperationReceipt?> TryCommitPreferencesAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantPreferenceCommit commit, AuthorizationAudit audit, CancellationToken ct = default);
}
