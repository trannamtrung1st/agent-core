using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Ports;

// Technical lifecycle evidence, never an end-user inventory or a new Run authority.
public sealed record TenantRequestIsolationState(TenantSession Session, bool Ready, SandboxLease? Lease);
public sealed record TenantRequestIsolationPage(IReadOnlyList<TenantSessionRoot> Items, Guid? NextCursor);
public interface ITenantRequestIsolationStore
{
    ValueTask<bool> HasTenantRequestBoundaryAsync(TenantSessionRoot owner, CancellationToken ct = default);
    ValueTask<TenantRequestIsolationPage> ListTenantRequestIsolationAsync(Guid? after, int limit, CancellationToken ct = default);
    ValueTask<TenantRequestIsolationState?> ReadTenantRequestIsolationAsync(TenantSessionRoot owner, CancellationToken ct = default);
    ValueTask<bool> RequestTenantIsolationRetirementAsync(TenantSessionRoot owner, long expectedRevision, CancellationToken ct = default);
    ValueTask<bool> CompleteTenantIsolatedRequestAsync(TenantSessionRoot owner, long expectedRevision, DateTimeOffset at, CancellationToken ct = default);
}
public interface ITenantRequestIsolationControl
{
    ValueTask<bool> SettleRequestAsync(TenantSessionRoot owner, CancellationToken ct = default);
}
