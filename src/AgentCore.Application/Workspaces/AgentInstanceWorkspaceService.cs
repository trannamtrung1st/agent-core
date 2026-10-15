using System.Security.Cryptography;
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

    public ValueTask<AgentWorkspaceContent> InspectAsync(Guid owner, Guid itemId, int maxBytes = 0, CancellationToken cancellationToken = default) =>
        lifecycle.WithInstanceAsync(owner, async ct => { await RequireAsync(owner, false, ct); return await store.InspectAsync(owner, itemId, maxBytes, ct); }, cancellationToken);

    public ValueTask<AgentWorkspaceContent> ReadAsync(Guid owner, Guid? itemId = null, string? path = null, CancellationToken cancellationToken = default) =>
        lifecycle.WithInstanceAsync(owner, async ct =>
        {
            await RequireAsync(owner, false, ct);
            return await store.ReadAsync(owner, itemId, path is null ? null : AgentHomePath.Normalize(path), ct);
        }, cancellationToken);

    public ValueTask DeleteAsync(Guid owner, Guid itemId, long expectedRevision, CancellationToken cancellationToken = default) =>
        lifecycle.WithInstanceAsync(owner, async ct =>
        {
            await RequireAsync(owner, true, ct);
            if (expectedRevision < 1) throw AgentCoreErrors.Validation("expectedRevision is required.");
            await store.DeleteAsync(owner, itemId, expectedRevision, ct);
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
        long? expectedRevision, string? expectedSha256, CancellationToken cancellationToken = default)
    {
        var snapshot = await SessionAsync(sessionId, true, cancellationToken);
        var owner = snapshot.AgentInstanceId;

        path = AgentHomePath.Normalize(path); ValidateExpected(expectedRevision, expectedSha256);
        return await lifecycle.WithInstanceAsync(owner, async ct =>
        {
            await RequireAsync(owner, true, ct); await SessionAsync(sessionId, true, ct);
            await RequireSessionHomeAsync(sessionId, path, true, ct);
            return await store.WriteFileAsync(owner, path, contentType, bytes, sessionId, expectedRevision, expectedSha256, ct);
        }, cancellationToken);
    }

    public async ValueTask CommitBinaryAsync(Guid sessionId, string path, string contentType, ReadOnlyMemory<byte> bytes,
        string? expectedSha256, CancellationToken cancellationToken = default)
    {
        var snapshot = await SessionAsync(sessionId, true, cancellationToken);
        var owner = snapshot.AgentInstanceId;
        path = WorkspaceLogicalPath.Resolve(path, sessionId); ValidateExpected(null, expectedSha256);
        if (!AgentHomePath.IsHome(path) && !path.StartsWith(WorkspaceLogicalPath.WorkingDirectory + "/", StringComparison.Ordinal))
            throw AgentCoreErrors.Forbidden("Binary output requires /home or current Session /working.");
        await lifecycle.WithInstanceAsync(owner, async ct =>
        {
            await RequireAsync(owner, true, ct); await SessionAsync(sessionId, true, ct);
            await RequireSessionHomeAsync(sessionId, path, true, ct);
            if (AgentHomePath.IsHome(path)) await store.WriteFileAsync(owner, path, contentType, bytes, sessionId, null, expectedSha256, ct);
            else
            {
                await scratch.EnsureAsync(sessionId, snapshot.Definition, ct);
                await scratch.ImportAsync(sessionId, path, new WorkspaceTransfer([new("", false, contentType, bytes.ToArray())]), null, expectedSha256, ct);
            }
        }, cancellationToken);
    }

    public async ValueTask<WorkspacePatchResult> PatchAsync(Guid sessionId, string path, string expectedSha256,
        IReadOnlyList<WorkspaceTextEdit> edits, CancellationToken cancellationToken = default)
    {
        var snapshot = await SessionAsync(sessionId, true, cancellationToken);
        var owner = snapshot.AgentInstanceId;

        path = AgentHomePath.Normalize(path); ValidateExpected(null, expectedSha256);
        return await lifecycle.WithInstanceAsync<WorkspacePatchResult>(owner, async ct =>
        {
            await RequireAsync(owner, true, ct); await SessionAsync(sessionId, true, ct);
            await RequireSessionHomeAsync(sessionId, path, true, ct);
            var original = await store.ReadAsync(owner, null, path, ct);
            if (original.Item.Sha256Hex != expectedSha256) throw AgentCoreErrors.Conflict("Home file hash does not match expectedSha256.");
            var bytes = WorkspaceTextPatches.Apply(original.Bytes, edits);
            var updated = await store.WriteFileAsync(owner, path, original.Item.ContentType, bytes, sessionId,
                original.Item.Revision, expectedSha256, ct);
            return new(path, expectedSha256, updated.Sha256Hex, updated.ByteSize, edits.Count);
        }, cancellationToken);
    }

    public async ValueTask CopyAcrossScopesAsync(Guid sessionId, string source, string destination,
        long? expectedRevision, string? expectedSha256, CancellationToken cancellationToken = default)
    {
        var snapshot = await SessionAsync(sessionId, true, cancellationToken);
        var owner = snapshot.AgentInstanceId;

        source = WorkspaceLogicalPath.Resolve(source, sessionId); destination = WorkspaceLogicalPath.Resolve(destination, sessionId);
        var sourceHome = AgentHomePath.IsHome(source); var destinationHome = AgentHomePath.IsHome(destination);
        if (sourceHome == destinationHome || !(sourceHome ? destination : source).StartsWith("/workspace/working/", StringComparison.Ordinal))
            throw AgentCoreErrors.Forbidden("Cross-scope copy requires /home and current Session /working children.");
        ValidateExpected(expectedRevision, expectedSha256);
        await lifecycle.WithInstanceAsync(owner, async ct =>
        {
            await RequireAsync(owner, true, ct); await SessionAsync(sessionId, true, ct);
            await scratch.EnsureAsync(sessionId, snapshot.Definition, ct);
            await RequireSessionHomeAsync(sessionId, source, false, ct);
            await RequireSessionHomeAsync(sessionId, destination, true, ct);
            var transfer = sourceHome ? await store.ExportAsync(owner, source, ct) : await scratch.ExportAsync(sessionId, source, ct);
            if (destinationHome) await store.ImportAsync(owner, destination, transfer, sessionId, expectedRevision, expectedSha256, ct);
            else await scratch.ImportAsync(sessionId, destination, transfer, expectedRevision, expectedSha256, ct);
        }, cancellationToken);
    }

    // Detached executions may only retrieve when Core supplies a proven managed owner. Scratch boundary actions require a session.
    public async ValueTask<WorkspaceStructureResult> StructureAsync(Guid sessionId, IReadOnlyList<WorkspaceStructuralOperation> operations,
        string? expectedTreeSha256, CancellationToken cancellationToken = default)
    {
        var snapshot = await SessionAsync(sessionId, true, cancellationToken);
        var owner = snapshot.AgentInstanceId;
        operations = WorkspaceStructuralPaths.Normalize(sessionId, operations);
        if (expectedTreeSha256 is null || expectedTreeSha256.Length != 64
            || !expectedTreeSha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw AgentCoreErrors.Validation("List /home and supply its current expectedTreeSha256 before restructuring durable work.");
        return await lifecycle.WithInstanceAsync(owner, async ct =>
        {
            await RequireAsync(owner, true, ct); await SessionAsync(sessionId, true, ct);
            await RequireSessionHomeAsync(sessionId, "/home", true, ct);
            return await store.StructureAsync(owner, operations, expectedTreeSha256, ct);
        }, cancellationToken);
    }

    public async ValueTask<IReadOnlyList<WorkspaceNode>> ListNodesAsync(Guid owner, string prefix, CancellationToken ct) =>
        (await ListProjectionAsync(owner, prefix, ct)).Nodes;

    // Keep metadata and its whole-tree token in one lifecycle-protected projection.
    // Read every bounded metadata page so a large first folder cannot hide later root entries.
    public ValueTask<(IReadOnlyList<WorkspaceNode> Nodes, string? TreeSha256)> ListProjectionAsync(Guid owner, string prefix, CancellationToken cancellationToken) =>
        lifecycle.WithInstanceAsync<(IReadOnlyList<WorkspaceNode>, string?)>(owner, async ct =>
        {
            await RequireAsync(owner, false, ct);
            prefix = AgentHomePath.Normalize(prefix, false);
            var page = await store.ListAsync(owner, prefix, null, AgentWorkspaceLimits.MaxPageItems, ct);
            var token = page.TreeSha256;
            var nodes = new Dictionary<string, WorkspaceNode>(StringComparer.Ordinal);
            var scanned = 0;
            while (true)
            {
                scanned += page.Items.Count;
                if (scanned > WorkspaceStructureLimits.MaxEntries) throw AgentCoreErrors.WorkspaceQuotaExceeded();
                foreach (var item in page.Items)
                {
                    if (item.LogicalPath == prefix)
                    {
                        if (!item.Directory) nodes[prefix] = new(prefix, false, item.ByteSize, false);
                        continue;
                    }
                    var rest = item.LogicalPath[(prefix.Length + 1)..];
                    var slash = rest.IndexOf('/');
                    var path = slash < 0 ? item.LogicalPath : prefix + "/" + rest[..slash];
                    nodes[path] = new(path, slash >= 0 || item.Directory, slash >= 0 ? 0 : item.ByteSize, false);
                }
                if (page.NextPath is null) return (nodes.Values.OrderBy(n => n.LogicalPath, StringComparer.Ordinal).ToArray(), token);
                page = await store.ListAsync(owner, prefix, page.NextPath, AgentWorkspaceLimits.MaxPageItems, ct);
                if (page.TreeSha256 != token) throw AgentCoreErrors.Conflict("Home changed during listing. Reload its current tree.");
            }
        }, cancellationToken);

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

    private static void ValidateExpected(long? revision, string? sha)
    {
        if (revision is < 1 || sha is not null && (sha.Length != 64 || !sha.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw AgentCoreErrors.Validation("Expected revision/hash is invalid.");
    }
}
