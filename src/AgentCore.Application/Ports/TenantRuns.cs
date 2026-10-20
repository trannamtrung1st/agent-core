using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Ports;

public sealed record TenantInputInterruption(Guid RunId, long Revision);
public sealed record TenantHumanRunAdmission(AgentRun Run, ConversationEntry Input, long ExpectedSessionRevision, long ExpectedInstanceRevision, IReadOnlyList<AttachmentRecord>? Attachments = null,
    TenantInputInterruption? Interruption = null);
public sealed record TenantRunAdmissionResult(bool Created, AgentRun Run, Guid EntryId, long SessionRevision, AgentRun? Interrupted = null);

// Existing entry/Activation/Run rows, owning Session, security revision and audit commit atomically.
// Trusted internal data only; public use-case results project safe metadata.
public sealed record TenantRunPage(IReadOnlyList<AgentRun> Items, int Total, int Page, int PageSize);

public interface ITenantRunStore
{
    ValueTask<TenantRunPage> ListTenantSessionRunsAsync(Guid tenant, Guid? session, IReadOnlyList<Guid>? allowed, int page, int pageSize, CancellationToken ct = default, Guid? instanceId = null, IReadOnlyList<Guid>? allowedSessions = null) => throw new NotSupportedException();
    ValueTask<CompletionInboxItem?> FindTenantCompletionAsync(AgentRunOwner owner, Guid childRunId, CancellationToken ct = default) => throw new NotSupportedException();
    ValueTask<TenantSessionRoot?> ResolveTenantSessionOwnerAsync(Guid sessionId, CancellationToken ct = default);
    ValueTask<AgentRun?> TryCancelTenantRunAsync(TenantSecurityState next, long expectedSecurityRevision, Guid runId, long expectedRunRevision,
        TenantSessionInitiator initiator, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default, ConversationEntry? response = null);
    ValueTask<TenantRunAdmissionResult?> TryAdmitTenantHumanRunAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantHumanRunAdmission admission, TenantSessionInitiator initiator, AuthorizationAudit audit, CancellationToken ct = default);
    ValueTask<AgentRun?> FindTenantRunAsync(Guid tenantId, Guid runId, CancellationToken ct = default);
    ValueTask<IReadOnlyList<Guid>> ListTenantRunIdsAsync(Guid tenantId, Guid sessionId, CancellationToken ct = default);
}
