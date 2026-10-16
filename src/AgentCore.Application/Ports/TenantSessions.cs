using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Ports;

public sealed record TenantSessionRoot(Guid TenantId, Guid SessionId, Guid AgentInstanceId, Guid ExecutorPrincipalId,
    Guid CreatedByPrincipalId, DateTimeOffset CreatedAt);
public sealed record TenantSession(TenantSessionRoot Owner, SessionSnapshot Snapshot);
public sealed record TenantSessionPage(IReadOnlyList<TenantSession> Items, int Total, int Page, int PageSize);
public sealed record TenantSessionInitiator(Guid AccountId, Guid AuthenticationSessionId, Guid PrincipalId)
    : HumanSessionReference(AccountId, AuthenticationSessionId, PrincipalId);

// Existing Session rows and immutable ownership commit with security/audit. No legacy adoption.
public interface ITenantSessionStore
{
    ValueTask<TenantSession?> FindTenantSessionAsync(Guid tenantId, Guid sessionId, CancellationToken ct = default);
    ValueTask<TenantSessionPage> ListTenantSessionsAsync(Guid tenantId, IReadOnlyList<Guid>? allowedIds, int page, int pageSize, CancellationToken ct = default);
    ValueTask<bool> TryCreateTenantSessionAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantSessionRoot owner, SessionSnapshot snapshot, TenantSessionInitiator initiator,
        long expectedInstanceRevision, AuthorizationAudit audit, CancellationToken ct = default);
}
