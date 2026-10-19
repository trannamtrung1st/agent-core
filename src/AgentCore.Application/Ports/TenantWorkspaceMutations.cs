using AgentCore.Domain.Security;

namespace AgentCore.Application.Ports;

public sealed record TenantWorkspaceAccess(TenantWorkspaceReference Workspace, long SecurityRevision, long? InstanceRevision,
    bool CanRead, bool CanWrite, bool CanDelete, bool ReadOnly, string? InstanceName, string? SessionTitle);

public sealed record TenantWorkspaceHumanAuthorization(TenantWorkspaceReference Workspace, HumanSessionReference Human,
    long SecurityRevision, long InstanceRevision, string Permission, AuthorizationAudit Audit);

public interface ITenantWorkspaceMutationStore
{
    ValueTask WriteTenantAsync(TenantWorkspaceReference owner, string path, ReadOnlyMemory<byte> bytes,
        Func<CancellationToken, ValueTask> authorizeCommit, CancellationToken ct = default);
    // The byte processor is pure; ownership, quotas, source comparison and commit authority remain in this filesystem owner.
    ValueTask TransformOfficeTenantAsync(TenantWorkspaceReference owner, string path, bool create,
        Func<ReadOnlyMemory<byte>, CancellationToken, byte[]> process, Func<CancellationToken, ValueTask> authorizeCommit, CancellationToken ct = default);
    ValueTask DeleteTenantAsync(TenantWorkspaceReference owner, string path,
        Func<CancellationToken, ValueTask> authorizeCommit, CancellationToken ct = default);
    // Callbacks are repeatable current-authority checks, never permission snapshots.
    // Patch/copy callers must independently authorize source reads as well as destination writes.
    ValueTask<WorkspacePatchResult> PatchTenantAsync(TenantWorkspaceReference owner, string path, IReadOnlyList<WorkspaceTextEdit> edits,
        Func<CancellationToken, ValueTask> authorizeCommit, CancellationToken ct = default);
    ValueTask<WorkspaceStructureResult> StructureTenantAsync(TenantWorkspaceReference owner, IReadOnlyList<WorkspaceStructuralOperation> operations,
        Func<CancellationToken, ValueTask> authorizeCommit, CancellationToken ct = default);
    ValueTask CopyTenantAsync(TenantWorkspaceReference sourceOwner, string source, TenantWorkspaceReference destinationOwner, string destination,
        Func<CancellationToken, ValueTask> authorizeCommit, CancellationToken ct = default);
}
