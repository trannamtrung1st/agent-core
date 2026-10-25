using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Ports;

public sealed record TenantSessionRoot(Guid TenantId, Guid SessionId, Guid AgentInstanceId, Guid ExecutorPrincipalId,
    Guid CreatedByPrincipalId, DateTimeOffset CreatedAt);
public sealed record TenantSession(TenantSessionRoot Owner, SessionSnapshot Snapshot);
public sealed record TenantSessionPage(IReadOnlyList<TenantSession> Items, int Total, int Page, int PageSize);
public sealed record TenantSessionRetentionTarget(TenantSessionRoot Owner, long Revision, DateTimeOffset DueAtUtc);
public sealed record TenantSessionInitiator(Guid AccountId, Guid AuthenticationSessionId, Guid PrincipalId)
    : HumanSessionReference(AccountId, AuthenticationSessionId, PrincipalId);
public enum TenantSessionAction { Rename, Archive, Restore, Pause, Resume, Complete, Cancel, Delete, SelectModel, SetAgentCompletion }
public sealed record TenantSessionChange(Guid SessionId, long ExpectedSessionRevision, TenantSessionAction Action, string? Title = null, SessionModelSelection? Model = null, bool? AgentCompletionAllowed = null);

// Existing Session rows and immutable ownership commit with security/audit. No legacy adoption.
public interface ITenantSessionStore
{
    ValueTask<IReadOnlyList<TenantSessionRetentionTarget>> ListDueSessionsAsync(TenantSessionRetentionTarget? after, int limit, CancellationToken ct = default);
    ValueTask<TenantSessionRetentionTarget?> FindExpiredSessionAsync(Guid tenant, Guid session, CancellationToken ct = default);
    // Internal system actor. Caller must hold the incumbent host's quiescent reservation.
    ValueTask<bool> RetireDueSessionAsync(TenantSessionRetentionTarget target, Guid auditId, CancellationToken ct = default);
    ValueTask<TenantSession?> TryChangeTenantSessionAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantSessionChange change, TenantSessionInitiator initiator, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default);
    ValueTask<TenantSession?> FindTenantSessionAsync(Guid tenantId, Guid sessionId, CancellationToken ct = default);
    ValueTask<TenantSessionPage> ListTenantSessionsAsync(Guid tenantId, IReadOnlyList<Guid>? allowedIds, int page, int pageSize, CancellationToken ct = default, bool includeArchived = true, SessionSurface? surface = null, Guid? instanceId = null, bool recentFirst = false);
    ValueTask<bool> TryCreateTenantSessionAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantSessionRoot owner, SessionSnapshot snapshot, TenantSessionInitiator initiator,
        long expectedInstanceRevision, AuthorizationAudit audit, CancellationToken ct = default);
}
