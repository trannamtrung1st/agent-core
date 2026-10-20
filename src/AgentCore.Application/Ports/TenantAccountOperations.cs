using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Ports;

public sealed record TenantAccountDispatch(Guid TenantId, Guid RequestId, Guid OperationId, Guid RunId, string ActionHash, AgentRunExternalEffect Origin);
public sealed record TenantAccountEffectStart(TenantAccountDispatch? Fence, TenantOperationReceipt? Receipt);
public sealed record TenantAccountItemResult(string FileId, string Status);
public sealed record TenantAccountActionResult(string Status, string? DraftId = null, string? FileId = null,
    string? Path = null, long? ByteSize = null, string? Sha256 = null, IReadOnlyList<TenantAccountItemResult>? Items = null);

// Narrow facet of the existing IAM/Run/receipt owner. Provider and filesystem I/O
// stay outside this transaction; a reserved uncertain effect is never replayed.
public interface ITenantAccountOperationStore
{
    ValueTask<TenantAccountEffectStart?> TryStartAccountEffectAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantOperationCommit commit, AuthorizationAudit audit, CancellationToken ct = default);
    ValueTask<TenantOperationReceipt?> TrySettleAccountEffectAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantAccountDispatch fence, TenantOperationOutcome outcome, string reason, AuthorizationAudit audit,
        bool recovery = false, CancellationToken ct = default, TenantAccountActionResult? result = null);
    ValueTask<IReadOnlyList<TenantAccountDispatch>> RecoverableAccountEffectsAsync(DateTimeOffset at, int limit, CancellationToken ct = default);
    ValueTask<bool> TryMaintainAccountEffectAsync(TenantSecurityState next, long expectedSecurityRevision, TenantAccountDispatch fence,
        long credentialRevision, long secretRevision, CredentialOAuthCheck maintenance, AuthorizationAudit audit, CancellationToken ct = default);
}
