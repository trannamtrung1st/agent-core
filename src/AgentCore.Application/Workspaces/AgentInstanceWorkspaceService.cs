using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Workspaces;

public sealed class AgentInstanceWorkspaceService(
    IAgentInstanceStore instances,
    IAgentInstanceWorkspaceStore store,
    IMemoryStore sessions,
    ISessionWorkspace scratch,
    AdminLifecycleCoordinator lifecycle,
    IAgentDefinitionStore? definitions = null)
{
    public ValueTask<AgentWorkspacePage> ListAsync(Guid owner, string prefix = "/home", string? afterPath = null,
        int limit = AgentWorkspaceLimits.MaxPageItems, CancellationToken cancellationToken = default) =>
        lifecycle.WithInstanceAsync(owner, async ct =>
        {
            await RequireAsync(owner, false, ct);
            if (limit < 1 || limit > AgentWorkspaceLimits.MaxPageItems) throw AgentCoreErrors.Validation("Invalid workspace page size.");
            return await store.ListAsync(owner, AgentHomePath.Normalize(prefix, false), afterPath is null ? null : AgentHomePath.Normalize(afterPath), limit, ct);
        }, cancellationToken);

    public ValueTask<AgentWorkspaceContent> InspectAsync(Guid owner, string path, int maxBytes = 0, CancellationToken cancellationToken = default) =>
        lifecycle.WithInstanceAsync(owner, async ct => { await RequireAsync(owner, false, ct); return await store.InspectAsync(owner, AgentHomePath.Normalize(path), maxBytes, ct); }, cancellationToken);

    public ValueTask<AgentWorkspacePage> FindFilesAsync(Guid owner, string query, string? cursor, int limit, CancellationToken cancellationToken = default) =>
        lifecycle.WithInstanceAsync(owner, async ct =>
        {
            await RequireAsync(owner, false, ct);
            return await store.FindFilesAsync(owner, query, cursor, limit, ct);
        }, cancellationToken);

    public ValueTask<AgentWorkspaceContent> ReadAsync(Guid owner, string path, CancellationToken cancellationToken = default) =>
        lifecycle.WithInstanceAsync(owner, async ct =>
        {
            await RequireAsync(owner, false, ct);
            return await store.ReadAsync(owner, AgentHomePath.Normalize(path), ct);
        }, cancellationToken);

    public ValueTask DeleteAsync(Guid owner, string path, CancellationToken cancellationToken = default) =>
        lifecycle.WithInstanceAsync(owner, async ct =>
        {
            await RequireAsync(owner, true, ct);
            await store.DeleteAsync(owner, AgentHomePath.Normalize(path), ct);
        }, cancellationToken);

    public async ValueTask<bool> SessionWritableAsync(Guid sessionId, CancellationToken ct)
    {
        var snapshot = await SessionAsync(sessionId, false, ct);
        if (snapshot.ArchivedAt is not null || snapshot.Status == SessionStatus.Ended) return false;
        var owner = snapshot.AgentInstanceId;
        var instance = await RequireAsync(owner, false, ct);
        return instance.Lifecycle == AgentInstanceLifecycle.Active;
    }

    public async ValueTask<Guid> SessionOwnerAsync(Guid sessionId, CancellationToken ct = default)
    {
        var snapshot = await SessionAsync(sessionId, false, ct);
        var owner = snapshot.AgentInstanceId;
        await RequireAsync(owner, false, ct);
        return owner;
    }

    public async ValueTask<AgentWorkspaceItem> WriteAsync(Guid sessionId, string path, string contentType, ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await SessionAsync(sessionId, true, cancellationToken);
        var owner = snapshot.AgentInstanceId;

        path = AgentHomePath.Normalize(path);
        return await lifecycle.WithInstanceAsync(owner, async ct =>
        {
            await RequireAsync(owner, true, ct); await SessionAsync(sessionId, true, ct);
            await RequireSessionHomeAsync(sessionId, path, true, ct);
            return await store.WriteFileAsync(owner, path, bytes, ct);
        }, cancellationToken);
    }

    public async ValueTask CommitBinaryAsync(Guid sessionId, string path, string contentType, ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await SessionAsync(sessionId, true, cancellationToken);
        var owner = snapshot.AgentInstanceId;
        path = WorkspaceLogicalPath.Resolve(path, sessionId);
        if (!AgentHomePath.IsHome(path) && !path.StartsWith(WorkspaceLogicalPath.WorkingDirectory + "/", StringComparison.Ordinal))
            throw AgentCoreErrors.Forbidden("Binary output requires /home or current Session /working.");
        await lifecycle.WithInstanceAsync(owner, async ct =>
        {
            await RequireAsync(owner, true, ct); await SessionAsync(sessionId, true, ct);
            await RequireSessionHomeAsync(sessionId, path, true, ct);
            if (AgentHomePath.IsHome(path)) await store.WriteFileAsync(owner, path, bytes, ct);
            else
            {
                await scratch.EnsureAsync(sessionId, snapshot.Definition, ct);
                await scratch.WriteAsync(sessionId, path, bytes, ct);
            }
        }, cancellationToken);
    }

    public async ValueTask<WorkspacePatchResult> PatchAsync(Guid sessionId, string path,
        IReadOnlyList<WorkspaceTextEdit> edits, CancellationToken cancellationToken = default)
    {
        var snapshot = await SessionAsync(sessionId, true, cancellationToken);
        var owner = snapshot.AgentInstanceId;

        path = AgentHomePath.Normalize(path);
        return await lifecycle.WithInstanceAsync<WorkspacePatchResult>(owner, async ct =>
        {
            await RequireAsync(owner, true, ct); await SessionAsync(sessionId, true, ct);
            await RequireSessionHomeAsync(sessionId, path, true, ct);
            var original = await store.ReadAsync(owner, path, ct);
            var bytes = WorkspaceTextPatches.Apply(original.Bytes, edits);
            var updated = await store.WriteFileAsync(owner, path, bytes, ct);
            return new(path, updated.ByteSize, edits.Count);
        }, cancellationToken);
    }

    public async ValueTask CopyAcrossScopesAsync(Guid sessionId, string source, string destination, CancellationToken cancellationToken = default)
    {
        var snapshot = await SessionAsync(sessionId, true, cancellationToken);
        var owner = snapshot.AgentInstanceId;

        source = WorkspaceLogicalPath.Resolve(source, sessionId); destination = WorkspaceLogicalPath.Resolve(destination, sessionId);
        var sourceHome = AgentHomePath.IsHome(source); var destinationHome = AgentHomePath.IsHome(destination);
        if (sourceHome == destinationHome || !(sourceHome ? destination : source).StartsWith("/working/", StringComparison.Ordinal))
            throw AgentCoreErrors.Forbidden("Cross-scope copy requires /home and current Session /working children.");
        await lifecycle.WithInstanceAsync(owner, async ct =>
        {
            await RequireAsync(owner, true, ct); await SessionAsync(sessionId, true, ct);
            await scratch.EnsureAsync(sessionId, snapshot.Definition, ct);
            await RequireSessionHomeAsync(sessionId, source, false, ct);
            await RequireSessionHomeAsync(sessionId, destination, true, ct);
            await scratch.CopyAcrossScopesAsync(sessionId, source, destination, ct);
        }, cancellationToken);
    }

    // Detached executions may only retrieve when Core supplies a proven managed owner. Scratch boundary actions require a session.
    public async ValueTask<WorkspaceStructureResult> StructureAsync(Guid sessionId, IReadOnlyList<WorkspaceStructuralOperation> operations, CancellationToken cancellationToken = default)
    {
        var snapshot = await SessionAsync(sessionId, true, cancellationToken);
        var owner = snapshot.AgentInstanceId;
        operations = WorkspaceStructuralPaths.Normalize(sessionId, operations);
        return await lifecycle.WithInstanceAsync(owner, async ct =>
        {
            await RequireAsync(owner, true, ct); await SessionAsync(sessionId, true, ct);
            await RequireSessionHomeAsync(sessionId, "/home", true, ct);
            return await store.StructureAsync(owner, operations, ct);
        }, cancellationToken);
    }

    public ValueTask<IReadOnlyList<WorkspaceNode>> ListNodesAsync(Guid owner, string prefix, CancellationToken cancellationToken) =>
        lifecycle.WithInstanceAsync<IReadOnlyList<WorkspaceNode>>(owner, async ct =>
        {
            await RequireAsync(owner, false, ct);
            var page = await store.ListAsync(owner, AgentHomePath.Normalize(prefix, false), null, AgentWorkspaceLimits.MaxPageItems, ct);
            return page.Items.Select(item => new WorkspaceNode(item.LogicalPath, item.Directory, item.ByteSize, false,
                item.Kind, item.UpdatedAt, item.LinkTarget, item.Mode)).ToArray();
        }, cancellationToken);

    public ValueTask<WorkspaceNodePage> ListNodePageAsync(Guid owner, string prefix, string? cursor, int limit, CancellationToken cancellationToken) =>
        lifecycle.WithInstanceAsync<WorkspaceNodePage>(owner, async ct =>
        {
            await RequireAsync(owner, false, ct);
            if (limit is < 1 or > AgentWorkspaceLimits.MaxPageItems) throw AgentCoreErrors.Validation("Invalid home page size.");
            prefix = AgentHomePath.Normalize(prefix, false);
            if (cursor is not null && !cursor.StartsWith(prefix + "/", StringComparison.Ordinal)) throw AgentCoreErrors.Validation("Cursor must belong to the listed directory.");
            var page = await store.ListAsync(owner, prefix, cursor, limit, ct);
            return new(page.Items.Select(item => new WorkspaceNode(item.LogicalPath, item.Directory, item.ByteSize, false,
                item.Kind, item.UpdatedAt, item.LinkTarget, item.Mode)).ToArray(), page.NextPath, page.Truncated);
        }, cancellationToken);

    public async ValueTask RequireSessionDirectoryAsync(Guid sessionId, string path, CancellationToken ct = default)
    {
        await RequireSessionHomeAsync(sessionId, path, false, ct);
        await store.RequireDirectoryAsync(await SessionOwnerAsync(sessionId, ct), path, ct);
    }

    public async ValueTask RequireSessionHomeAsync(Guid sessionId, string path, bool write, CancellationToken ct = default)
    {
        if (!AgentHomePath.IsHome(path)) return;
        var snapshot = await SessionAsync(sessionId, write, ct);
        var instance = await RequireAsync(snapshot.AgentInstanceId, write, ct);
        var baseline = definitions is null ? snapshot.Definition : await definitions.GetAsync(instance.DefinitionId, instance.ActiveVersion, ct)
            ?? throw AgentCoreErrors.Persistence("Current Definition is unavailable.");
        var current = AgentCore.Application.Agents.InstanceSettingsResolver.Resolve(baseline, instance.SettingsOverrides);
        AgentCore.Application.Agents.StateIsolationAccess.RequireHome(current.StateIsolationPolicy ?? StateIsolationPolicy.Default, path, write);
    }

    private async ValueTask<AgentInstance> RequireAsync(Guid owner, bool mutation, CancellationToken ct)
    {
        var instance = await instances.FindAsync(owner, ct) ?? throw AgentCoreErrors.NotFound("Agent instance was not found.");
        if (mutation && instance.Lifecycle != AgentInstanceLifecycle.Active) throw AgentCoreErrors.Conflict("Archived workspace is read-only.");
        return instance;
    }

    private async ValueTask<SessionSnapshot> SessionAsync(Guid id, bool mutation, CancellationToken ct)
    {
        var snapshot = await sessions.LoadMetadataAsync(id, ct) ?? throw AgentCoreErrors.NotFound("Session was not found.");
        if (snapshot.DurablyDeletedAt is not null) throw AgentCoreErrors.NotFound("Session was not found.");
        if (mutation && (snapshot.Status == SessionStatus.Ended || snapshot.ArchivedAt is not null))
            throw AgentCoreErrors.Conflict("Ended or archived sessions cannot change workspace state.");
        return snapshot;
    }

}
