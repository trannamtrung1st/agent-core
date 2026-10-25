using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Domain.Spaces;

namespace AgentCore.Application.Ports;

public sealed record SpaceWorkspaceCleanupPage(IReadOnlyList<TenantWorkspaceReference.SpaceHome> Items, string? NextCursor);

public interface ITenantSpaceWorkspaceCleanup
{
    ValueTask PurgeDeletedSpaceAsync(TenantWorkspaceReference.SpaceHome owner, CancellationToken ct = default);
}

public sealed record SpacePage(IReadOnlyList<Space> Items, int Total, int Page, int PageSize);
public sealed record SpaceChange(Space Space, long ExpectedRevision, SpaceMembership? Membership = null,
    long? ExpectedMembershipRevision = null, bool RemoveMembership = false, bool Delete = false);

/// <summary>Space, memberships, tenant security revision and audit share one transaction owner.</summary>
public interface ITenantSpaceStore
{
    ValueTask<SpaceWorkspaceCleanupPage> ReadDeletedSpaceWorkspacePageAsync(string? cursor, int limit, CancellationToken ct = default) => throw new NotSupportedException();
    ValueTask<bool> CanPurgeDeletedSpaceWorkspaceAsync(TenantWorkspaceReference.SpaceHome owner, CancellationToken ct = default) => throw new NotSupportedException();
    ValueTask<Space?> FindSpaceAsync(Guid tenant, Guid space, CancellationToken ct = default);
    ValueTask<Space?> FindPrivateSpaceAsync(Guid tenant, Guid instance, CancellationToken ct = default);
    ValueTask<SpacePage> ListSpacesAsync(Guid tenant, IReadOnlyList<Guid>? allowedSpaces, int page, int pageSize, CancellationToken ct = default);
    ValueTask<IReadOnlyList<SpaceMembership>> ReadMembershipsAsync(Guid tenant, Guid space, CancellationToken ct = default);
    ValueTask<bool> TryChangeSpaceAsync(TenantSecurityState next, long expectedSecurityRevision, SpaceChange change,
        AuthorizationAudit audit, HumanSessionReference? initiator = null, CancellationToken ct = default);
}
