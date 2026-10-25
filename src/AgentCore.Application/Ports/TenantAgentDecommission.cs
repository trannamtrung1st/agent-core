using AgentCore.Application.Admin;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Ports;

public sealed record TenantAgentDecommissionSnapshot(TenantAgent Agent, IdentityStatus Status,
    AdminDeletionReferenceCounts References, AuthorizationAudit? Receipt);
public sealed record TenantAgentDecommissionPage(IReadOnlyList<TenantBrowserAgentOwner> Items, Guid? NextCursor);
public interface ITenantAgentDecommissionStore
{
    ValueTask<TenantAgentDecommissionSnapshot?> ReadAgentDecommissionAsync(Guid tenant, Guid instance, CancellationToken ct = default);
    ValueTask<bool> TryDecommissionAgentAsync(Guid tenant, Guid instance, HumanSessionReference human,
        long expectedSecurityRevision, long expectedInstanceRevision, AuthorizationAudit audit, CancellationToken ct = default);
    ValueTask<TenantAgentDecommissionPage> ListAgentDecommissionCleanupAsync(Guid? afterInstance, int limit, CancellationToken ct = default);
    ValueTask<bool> CanPurgeDecommissionedAgentWorkspaceAsync(TenantBrowserAgentOwner owner, CancellationToken ct = default);
    ValueTask<TenantWorkspaceReference.SpaceHome?> ReadDecommissionedPrivateWorkspaceAsync(TenantBrowserAgentOwner owner, CancellationToken ct = default);
    ValueTask<bool> IsAgentDecommissionedAsync(TenantBrowserAgentOwner owner, CancellationToken ct = default);
}
public interface ITenantAgentWorkspaceCleanup
{
    ValueTask PurgeDecommissionedAgentAsync(TenantBrowserAgentOwner owner, Func<CancellationToken, ValueTask<bool>> stillDecommissioned, CancellationToken ct = default);
}
