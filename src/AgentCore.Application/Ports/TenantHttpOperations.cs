using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Ports;

public sealed record TenantHttpDispatch(Guid TenantId,Guid RequestId,Guid OperationId,Guid RunId,string ActionHash,AgentRunExternalEffect Origin);
public sealed record TenantHttpEffectStart(TenantHttpDispatch? Fence,TenantOperationReceipt? Receipt);
public sealed record TenantHttpResult(int StatusCode,string FinalUrl,string? ContentType,string Body,bool Truncated);
public sealed record TenantHttpApprovalVisibility(IReadOnlyList<Guid>? Instances,IReadOnlyList<Guid>? Sessions);

// A narrow facet of the existing Run/approval/grant/receipt transaction owner.
public interface ITenantHttpOperationStore
{
    ValueTask<TenantHttpEffectStart?> TryStartHttpEffectAsync(TenantSecurityState next,long expectedSecurityRevision,TenantOperationCommit commit,AuthorizationAudit audit,CancellationToken ct=default);
    ValueTask<TenantOperationReceipt?> TrySettleHttpEffectAsync(TenantSecurityState next,long expectedSecurityRevision,TenantHttpDispatch fence,TenantOperationOutcome outcome,string reason,AuthorizationAudit audit,
        bool recovery=false,CancellationToken ct=default,TenantHttpResult? result=null);
    ValueTask<IReadOnlyList<TenantHttpDispatch>> RecoverableHttpEffectsAsync(DateTimeOffset at,int limit,CancellationToken ct=default);
}
