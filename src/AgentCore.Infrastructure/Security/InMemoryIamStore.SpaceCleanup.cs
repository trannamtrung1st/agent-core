using AgentCore.Application.Ports;
using AgentCore.Application.Workspaces;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Spaces;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    public ValueTask<SpaceWorkspaceCleanupPage> ReadDeletedSpaceWorkspacePageAsync(string? cursor, int limit, CancellationToken ct = default)
    {
        SpaceWorkspaceCleanupCursor.Validate(cursor, limit); ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var items = _spaces.Values.Where(s => s.Kind == SpaceKind.Shared && s.DeletedAt is not null
                    && (cursor is null || string.CompareOrdinal(SpaceWorkspaceCleanupCursor.Of(s.TenantId, s.SpaceId), cursor) > 0))
                .OrderBy(s => SpaceWorkspaceCleanupCursor.Of(s.TenantId, s.SpaceId), StringComparer.Ordinal).Take(limit + 1).ToArray();
            return ValueTask.FromResult(new SpaceWorkspaceCleanupPage(items.Take(limit).Select(s => new TenantWorkspaceReference.SpaceHome(s.TenantId, s.SpaceId)).ToArray(),
                items.Length > limit ? SpaceWorkspaceCleanupCursor.Of(items[limit - 1].TenantId, items[limit - 1].SpaceId) : null));
        }
    }
    public ValueTask<bool> CanPurgeDeletedSpaceWorkspaceAsync(TenantWorkspaceReference.SpaceHome owner, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (sandboxBacking.TenantGate)
            return ValueTask.FromResult(!owner.PrivateAlias && _spaces.GetValueOrDefault((owner.TenantId, owner.SpaceId)) is { Kind: SpaceKind.Shared, DeletedAt: not null }
                && !_spaceMemberships.Keys.Any(k => k.Tenant == owner.TenantId && k.Space == owner.SpaceId)
                && !HasSpaceWork(owner.TenantId, owner.SpaceId)
                && !sandboxBacking.HasUnretiredSpaceLease(owner.TenantId, owner.SpaceId));
    }
}
