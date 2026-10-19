using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Ports;

public sealed record TenantWorkspaceDispatch(Guid TenantId, Guid RunId, AgentRunOwner Owner, Guid SessionId, string ActionHash, AgentRunExternalEffect Origin);
public sealed record TenantWorkspaceEffectStart(TenantWorkspaceDispatch? Fence, TenantOperationReceipt? Receipt);
public sealed record TenantWorkspaceEffectCommit(Guid RunId, Guid Generation, long ExpectedRunRevision, AgentRunDirectOperation Operation, string ActionHash);

// The existing Run side-effect and immutable receipt owners; filesystem I/O stays outside their transaction.
public interface ITenantWorkspaceOperationStore : ITenantDirectOperationStore
{
    ValueTask<TenantWorkspaceEffectStart?> TryStartWorkspaceEffectAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantWorkspaceEffectCommit commit, AuthorizationAudit audit, CancellationToken ct = default);
    ValueTask<TenantOperationReceipt?> TrySettleWorkspaceEffectAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantWorkspaceDispatch fence, TenantOperationOutcome outcome, string reason, AuthorizationAudit audit, bool recovery = false, CancellationToken ct = default);
    ValueTask<IReadOnlyList<TenantWorkspaceDispatch>> RecoverableWorkspaceEffectsAsync(DateTimeOffset at, int limit, CancellationToken ct = default);
}
