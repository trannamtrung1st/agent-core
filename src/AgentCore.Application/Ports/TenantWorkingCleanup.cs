using AgentCore.Domain.Conversation;
using AgentCore.Domain.Tasks;

namespace AgentCore.Application.Ports;

// Bounded retained facts shared by technical request settlement and working-file retention.
public sealed record TenantSessionSettlement(TenantSession Session, IReadOnlyList<AgentRun> Runs,
    IReadOnlyList<TenantSession> Children, IReadOnlyList<AgentRun> ChildRuns,
    IReadOnlyList<CompletionInboxItem> Receipts, bool Complete = true)
{
    public IReadOnlyList<DelegatedTask> Tasks { get; init; } = [];
    public IReadOnlyList<TaskNotification> TaskNotifications { get; init; } = [];
}
public sealed record TenantWorkingCleanupTarget(TenantSessionRoot Owner, long SessionRevision, DateTimeOffset? DeletedAt,
    Guid? PolicyRunId, long? PolicyRunRevision, Guid? LeaseGeneration, long? LeaseRevision);
public sealed record TenantWorkingCleanupPage(IReadOnlyList<TenantSessionRoot> Items, Guid? NextCursor);

// No human-selected path or new execution authority. Retained lifecycle/policy and settlement authorize bytes and deleted-Session Run minimization only.
public interface ITenantWorkingCleanupStore
{
    ValueTask<TenantWorkingCleanupPage> ListTenantWorkingCleanupAsync(Guid? after, int limit, CancellationToken ct = default);
    ValueTask<TenantWorkingCleanupTarget?> ReadTenantWorkingCleanupAsync(TenantSessionRoot owner, CancellationToken ct = default);
    // Reuses the retained deletion witness to recover pre-fix content and deferred report bodies.
    ValueTask<bool> RetireTenantRunContentAsync(TenantSessionRoot owner, CancellationToken ct = default);
}
public interface ITenantWorkingCleanupBytes
{
    ValueTask<bool> PurgeTenantWorkingAsync(TenantWorkingCleanupTarget target,
        Func<CancellationToken, ValueTask<bool>> stillAllowed, CancellationToken ct = default);
}
