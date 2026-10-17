using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Ports;

public sealed record TenantHumanRunAdmission(AgentRun Run, ConversationEntry Input, long ExpectedSessionRevision, long ExpectedInstanceRevision);
public sealed record TenantRunAdmissionResult(bool Created, AgentRun Run, Guid EntryId, long SessionRevision);

// Existing entry/Activation/Run rows, owning Session, security revision and audit commit atomically.
// Trusted internal data only; public use-case results project safe metadata.
public interface ITenantRunStore
{
    ValueTask<AgentRun?> TryCancelTenantRunAsync(TenantSecurityState next, long expectedSecurityRevision, Guid runId, long expectedRunRevision,
        TenantSessionInitiator initiator, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default);
    ValueTask<TenantRunAdmissionResult?> TryAdmitTenantHumanRunAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantHumanRunAdmission admission, TenantSessionInitiator initiator, AuthorizationAudit audit, CancellationToken ct = default);
    ValueTask<AgentRun?> FindTenantRunAsync(Guid tenantId, Guid runId, CancellationToken ct = default);
    ValueTask<IReadOnlyList<Guid>> ListTenantRunIdsAsync(Guid tenantId, Guid sessionId, CancellationToken ct = default);
}
