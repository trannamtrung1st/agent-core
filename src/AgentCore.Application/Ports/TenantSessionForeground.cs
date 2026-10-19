using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Ports;

// Server-prepared original-human references. A live Runtime may advance its own snapshot
// after preparation; only that exact generation may carry the surface change forward.
public sealed record TenantSessionForeground(TenantSessionRoot Owner, TenantSessionInitiator Human,
    long SecurityRevision, long ObservedSessionRevision, long InstanceRevision);

public interface ITenantSessionForegroundStore
{
    ValueTask SaveTenantRuntimeForegroundAsync(TenantRuntimeFrame frame, SessionSnapshot snapshot, long expectedRevision,
        TenantSessionForeground request, Guid auditId, CancellationToken ct = default);
    ValueTask ContinueTenantQuiescentAsync(TenantSessionForeground request, Guid auditId, CancellationToken ct = default);
}
public interface ITenantSessionForegroundControl
{
    ValueTask ContinueAsync(TenantSessionForeground request, CancellationToken ct = default);
}
public interface ITenantRuntimeForegroundWriter
{
    ValueTask SaveForegroundAsync(SessionSnapshot snapshot, long expectedRevision, TenantSessionForeground request, CancellationToken ct);
}
