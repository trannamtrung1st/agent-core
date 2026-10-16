using AgentCore.Domain.Definitions;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Workspaces;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Workspaces;

/// <summary>A thin owner-validated filesystem adapter; there are no file rows or blobs.</summary>
public sealed class FileAgentInstanceWorkspaceStore(string workspaceRoot, IAgentInstanceStore instances, ISandboxStore sandboxes,
    long maxFileBytes = AgentWorkspaceLimits.MaxFileBytes,
    long maxInstanceBytes = AgentWorkspaceLimits.MaxInstanceBytes) : IAgentInstanceWorkspaceStore
{
    private readonly string _root = AgentWorkspacePhysicalPaths.ValidateRoot(workspaceRoot);

    private async ValueTask<CanonicalWorkspaceFilesystem> FilesAsync(Guid owner, bool write, CancellationToken ct)
    {
        var instance = await instances.FindAsync(owner, ct) ?? throw AgentCoreErrors.NotFound("Agent Instance was not found.");
        if (write && instance.Lifecycle != AgentInstanceLifecycle.Active) throw AgentCoreErrors.Conflict("Archived workspace is read-only.");
        var files = new CanonicalWorkspaceFilesystem(AgentWorkspacePhysicalPaths.HomeDirectory(_root, owner), "/home");
        files.Ensure(); return files;
    }

    public async ValueTask<AgentWorkspacePage> ListAsync(Guid instanceId, string prefix, string? afterPath, int limit, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > AgentWorkspaceLimits.MaxPageItems) throw AgentCoreErrors.Validation("Invalid home page size.");
        prefix = AgentHomePath.Normalize(prefix, false);
        var files = await FilesAsync(instanceId, false, cancellationToken);
        var entry = files.Stat(prefix);
        var page = entry.Directory ? files.List(prefix, afterPath, limit)
            : new CanonicalWorkspaceFilesystem.Page([entry], null, false);
        CanonicalWorkspaceFilesystem.Usage? usage = null;
        try { usage = files.Measure(cancellationToken); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or AgentCoreException) { /* Report unknown, never a passing quota. */ }
        return new(page.Entries.Select(e => Item(instanceId, e)).ToArray(), usage?.Bytes, usage?.Entries,
            page.NextPath, usage?.Enforcement ?? "unknown", page.Truncated);
    }

    public ValueTask<AgentWorkspaceContent> InspectAsync(Guid instanceId, string path, int maxContentBytes, CancellationToken ct = default) =>
        ReadBoundedAsync(instanceId, path, maxContentBytes, ct, metadataOnlyOnLimit:true);
    public async ValueTask RequireDirectoryAsync(Guid instanceId, string path, CancellationToken ct = default) =>
        (await FilesAsync(instanceId, false, ct)).RequireDirectory(AgentHomePath.Normalize(path, false));
    public async ValueTask<AgentWorkspacePage> FindFilesAsync(Guid instanceId, string query, string? afterPath, int limit, CancellationToken ct = default)
    {
        if (query.Length > 200 || limit is < 1 or > 256) throw AgentCoreErrors.Validation("Invalid workspace search bound.");
        if (afterPath is not null) AgentHomePath.Normalize(afterPath.TrimEnd('/'), false);
        var files = await FilesAsync(instanceId, false, ct);
        var page = files.FindFiles(query, afterPath, limit, ct);
        return new(page.Entries.Select(entry => Item(instanceId, entry)).ToArray(), null, null, page.NextPath, "unknown", page.Truncated);
    }
    public ValueTask<AgentWorkspaceContent> ReadAsync(Guid instanceId, string path, CancellationToken cancellationToken = default) =>
        ReadBoundedAsync(instanceId, path, maxFileBytes, cancellationToken);
    private async ValueTask<AgentWorkspaceContent> ReadBoundedAsync(Guid owner, string path, long maxBytes, CancellationToken ct, bool metadataOnlyOnLimit = false)
    {
        path = AgentHomePath.Normalize(path);
        if (maxBytes < 0 || maxBytes > maxFileBytes) throw AgentCoreErrors.Validation("Invalid home content bound.");
        var files = await FilesAsync(owner, false, ct);
        var entry = files.Stat(path);
        if (entry.Directory) throw AgentCoreErrors.Validation("Read requires a file; list the directory instead.");
        if (!metadataOnlyOnLimit && entry.ByteSize > maxBytes) throw AgentCoreErrors.WorkspaceQuotaExceeded();
        var bytes = maxBytes == 0 || entry.ByteSize > maxBytes ? [] : await files.ReadAsync(path, maxBytes, ct);
        // Metadata describes this read's bytes rather than a cached file identity.
        return new(Item(owner, entry) with { ByteSize = bytes.Length == 0 ? entry.ByteSize : bytes.LongLength }, bytes);
    }

    public async ValueTask<AgentWorkspaceItem> WriteFileAsync(Guid instanceId, string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        path = AgentHomePath.Normalize(path);
        var files = await FilesAsync(instanceId, true, cancellationToken);
        await files.WriteAsync(path, bytes, maxFileBytes, maxInstanceBytes, cancellationToken);
        return Item(instanceId, files.Stat(path));
    }
    public async ValueTask DeleteAsync(Guid instanceId, string path, CancellationToken cancellationToken = default)
    {
        path = AgentHomePath.Normalize(path);
        var files = await FilesAsync(instanceId, true, cancellationToken);
        files.Delete(path, recursive: false, cancellationToken);
    }
    public async ValueTask<WorkspaceStructureResult> StructureAsync(Guid instanceId, IReadOnlyList<WorkspaceStructuralOperation> operations,
        CancellationToken cancellationToken = default)
    {
        var files = await FilesAsync(instanceId, true, cancellationToken);
        return files.Structure(operations, maxFileBytes, maxInstanceBytes, cancellationToken);
    }

    // The lifecycle coordinator calls this only after committed logical deletion
    // and verified process retirement. Do not recreate a missing owner on purge.
    public async ValueTask DeleteInstanceContentAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        if (await instances.FindAsync(instanceId, cancellationToken) is not null)
            throw AgentCoreErrors.Conflict("Existing Agent Instance content cannot be purged.");
        if ((await sandboxes.ListLeasesAsync(cancellationToken)).Any(lease => lease.AgentInstanceId == instanceId && lease.State != SandboxLeaseState.Terminated))
            throw AgentCoreErrors.Conflict("Live or uncertain sandbox mounts block Instance filesystem cleanup.");
        CanonicalWorkspaceFilesystem.PurgeOwner(AgentWorkspacePhysicalPaths.AgentRoot(_root, instanceId), cancellationToken);
    }
    public ValueTask DeleteInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) =>
        DeleteInstanceContentAsync(instanceId, cancellationToken);

    private static AgentWorkspaceItem Item(Guid owner, CanonicalWorkspaceFilesystem.Entry entry) =>
        new(owner, entry.Path, WorkspaceContentTypes.Of(entry.Path), entry.ByteSize, entry.ModifiedAt, entry.Directory, entry.Kind, entry.LinkTarget, entry.Mode);
}

internal static class WorkspaceValueTasks
{
    public static async ValueTask AsVoid<T>(this ValueTask<T> task) { await task; }
}
