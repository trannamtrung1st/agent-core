using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Ports;

// Bounded retained facts shared by technical request settlement and working-file retention.
public sealed record TenantSessionSettlement(TenantSession Session, IReadOnlyList<AgentRun> Runs,
    IReadOnlyList<TenantSession> Children, IReadOnlyList<AgentRun> ChildRuns,
    IReadOnlyList<CompletionInboxItem> Receipts, bool Complete = true);
public sealed record TenantWorkingCleanupTarget(TenantSessionRoot Owner, long SessionRevision, DateTimeOffset? DeletedAt,
    Guid? PolicyRunId, long? PolicyRunRevision, Guid? LeaseGeneration, long? LeaseRevision);
public sealed record TenantWorkingCleanupPage(IReadOnlyList<TenantSessionRoot> Items, Guid? NextCursor);

// No human-selected path or new execution authority. Retained lifecycle/policy and settlement authorize bytes only.
public interface ITenantWorkingCleanupStore
{
    ValueTask<TenantWorkingCleanupPage> ListTenantWorkingCleanupAsync(Guid? after, int limit, CancellationToken ct = default);
    ValueTask<TenantWorkingCleanupTarget?> ReadTenantWorkingCleanupAsync(TenantSessionRoot owner, CancellationToken ct = default);
}
public interface ITenantWorkingCleanupBytes
{
    ValueTask<bool> PurgeTenantWorkingAsync(TenantWorkingCleanupTarget target,
        Func<CancellationToken, ValueTask<bool>> stillAllowed, CancellationToken ct = default);
}
