using AgentCore.Domain.Definitions;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Workspaces;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Workspaces;

/// <summary>A thin owner-validated filesystem adapter; there are no file rows or blobs.</summary>
public sealed partial class FileSpaceWorkspaceStore(string workspaceRoot, ITenantSpaceStore tenantSpaces,
    ITenantAgentStore tenantAgents, ITenantSessionStore tenantSessions, ITenantAgentDecommissionStore decommissions, ISandboxStore sandboxes,
    long maxFileBytes = AgentWorkspaceLimits.MaxFileBytes, long maxSpaceBytes = AgentWorkspaceLimits.MaxSpaceBytes) : ITenantAgentWorkspaceCleanup, ITenantSpaceWorkspaceCleanup
{
    private readonly string _root = AgentWorkspacePhysicalPaths.ValidateRoot(workspaceRoot);

    // Logical deletion and verified process retirement precede physical cleanup.
    public async ValueTask PurgeDecommissionedAgentAsync(TenantBrowserAgentOwner owner, Func<CancellationToken, ValueTask<bool>> stillDecommissioned, CancellationToken ct = default)
    {
        owner.Validate();
        var privateHome = await decommissions.ReadDecommissionedPrivateWorkspaceAsync(owner, ct)
            ?? throw new AgentCore.Application.Security.AccessDeniedException("space_unavailable");
        if (!await stillDecommissioned(ct)) throw AgentCoreErrors.Conflict("Agent retirement is not committed.");
        if ((await sandboxes.ListLeasesAsync(ct)).Any(l => l.AgentInstanceId == owner.AgentInstanceId && l.State != SandboxLeaseState.Terminated))
            throw AgentCoreErrors.Conflict("Live or uncertain sandbox mounts block Instance filesystem cleanup.");
        if (!await stillDecommissioned(ct)) throw AgentCoreErrors.Conflict("Agent retirement changed.");
        CanonicalWorkspaceFilesystem.PurgeOwner(AgentWorkspacePhysicalPaths.AgentRoot(_root, owner.AgentInstanceId), ct);
        if (!await stillDecommissioned(ct)) throw AgentCoreErrors.Conflict("Agent retirement changed.");
        CanonicalWorkspaceFilesystem.PurgeOwner(AgentWorkspacePhysicalPaths.SpaceHomeDirectory(_root, privateHome.TenantId, privateHome.SpaceId), ct);
    }
    private static AgentWorkspaceItem Item(TenantWorkspaceReference owner, CanonicalWorkspaceFilesystem.Entry entry) =>
        new(owner, entry.Path, WorkspaceContentTypes.Of(entry.Path), entry.ByteSize, entry.ModifiedAt, entry.Directory, entry.Kind, entry.LinkTarget, entry.Mode);
}

internal static class WorkspaceValueTasks
{
    public static async ValueTask AsVoid<T>(this ValueTask<T> task) { await task; }
}
