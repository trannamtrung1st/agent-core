namespace AgentCore.Application.Ports;

public sealed record TenantArtifactCleanupTarget(TenantSessionRoot Owner, DateTimeOffset DeletedAt);
public sealed record TenantArtifactCleanupPage(IReadOnlyList<TenantArtifactCleanupTarget> Items, Guid? NextCursor);

// Technical worker port: committed deletion is the durable authority. No human/model call can
// select arbitrary paths or erase a live owner. Retain metadata and receipts; remove bytes only.
public interface ITenantArtifactCleanupStore
{
    ValueTask<TenantArtifactCleanupPage> ListDeletedArtifactOwnersAsync(Guid? after, int limit, CancellationToken ct = default);
    ValueTask<bool> CleanupDeletedArtifactsAsync(TenantArtifactCleanupTarget target, CancellationToken ct = default);
}
