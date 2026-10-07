using System.Security.Cryptography;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Workspaces;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Workspaces;

public sealed partial class FileAgentInstanceWorkspaceStore
{
    internal Func<int, CancellationToken, ValueTask>? BeforeStructuralOperation { get; set; }

    public ValueTask<WorkspaceStructureResult> StructureAsync(Guid instanceId,
        IReadOnlyList<WorkspaceStructuralOperation> operations, string expectedTreeSha256, CancellationToken cancellationToken = default) =>
        WithAsync(instanceId, async ct =>
        {
            var rows = await RowsAsync(instanceId, ct);
            if (expectedTreeSha256 != TreeHash(rows)) throw AgentCoreErrors.Conflict("Home tree changed. List the current tree before restructuring.");
            // Logical directories have no host counterparts. Every referenced file blob is guarded
            // before the complete batch is admitted, including children of recursive mutations.
            foreach (var row in rows.Where(r => !FromRow(r).Directory))
            {
                ct.ThrowIfCancellationRequested();
                var physical = BlobPath(instanceId, row.BlobKey);
                if (!File.Exists(physical) || new FileInfo(physical).Length != row.ByteSize)
                    throw AgentCoreErrors.Conflict("Home source integrity check failed before restructuring.");
            }
            var plan = WorkspaceTreePlanner.Plan(rows.Select(r => new WorkspaceTreeEntry(FromRow(r).LogicalPath, FromRow(r).Directory, r.ByteSize)).ToArray(),
                operations, "/home", maxInstanceBytes, maxFileBytes, ct, AgentWorkspaceLimits.MaxItems);
            var results = new List<WorkspaceOperationOutcome>();
            foreach (var step in plan)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (BeforeStructuralOperation is not null) await BeforeStructuralOperation(results.Count, ct);
                    await ApplyStructureAsync(instanceId, rows, step, ct);
                    rows = await RowsAsync(instanceId, ct);
                    results.Add(step.Outcome);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or AgentCoreException or DbUpdateException)
                {
                    return new WorkspaceStructureResult(false, plan.Count, results.Count, results.Count, [.. results, step.Outcome with { Status = "failed" }, .. plan.Skip(results.Count + 1).Select(p => p.Outcome with { Status = "notExecuted" })],
                        "executionFailed", "Home execution stopped. Reload current paths before retrying; earlier operations are retained.",
                        true);
                }
            }
            return new WorkspaceStructureResult(true, plan.Count, results.Count, null, results, TreeSha256: TreeHash(rows));
        }, cancellationToken);

    private async ValueTask ApplyStructureAsync(Guid owner, List<AgentWorkspaceRow> rows, WorkspacePlannedOperation step, CancellationToken ct)
    {
        var op = step.Operation;
        var sources = step.SourceTree.Select(e => e.Path).ToHashSet(StringComparer.Ordinal);
        var indexed = rows.Select(row => (Row: row, Item: FromRow(row))).ToDictionary(pair => pair.Item.LogicalPath, StringComparer.Ordinal);
        var knownPaths = indexed.Keys.ToHashSet(StringComparer.Ordinal);
        var removed = op.Op is "move" or "delete" ? indexed.Values.Where(pair => sources.Contains(pair.Item.LogicalPath)).Select(pair => pair.Row).ToArray() : [];
        var added = new List<AgentWorkspaceRow>(); var newBlobs = new List<string>();
        try
        {
            foreach (var parent in step.ParentsToCreate) AddDirectory(parent);
            if (op.Op == "mkdir") AddDirectory(op.Path!);
            if (op.Op is "copy" or "move")
            {
                foreach (var node in step.SourceTree)
                {
                    ct.ThrowIfCancellationRequested();
                    var destination = op.Destination! + node.Path[op.Source!.Length..];
                    if (!indexed.TryGetValue(node.Path, out var original))
                    {
                        if (!node.Directory) throw AgentCoreErrors.Conflict("Home source changed after preflight.");
                        AddDirectory(destination); continue;
                    }
                    var previous = original.Item;
                    var blob = original.Row.BlobKey;
                    if (op.Op == "copy" && !previous.Directory)
                    {
                        var physical = BlobPath(owner, blob);
                        if (!File.Exists(physical) || new FileInfo(physical).Length != previous.ByteSize)
                            throw AgentCoreErrors.Conflict("Home source integrity check failed.");
                        var bytes = await File.ReadAllBytesAsync(physical, ct);
                        if (Hash(bytes) != previous.Sha256Hex) throw AgentCoreErrors.Conflict("Home source integrity check failed.");
                        blob = ids.NewId().ToString("N");
                        var target = BlobPath(owner, blob); newBlobs.Add(target);
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!); target = BlobPath(owner, blob);
                        await WriteImmutableBlobAsync(target, bytes, ct);
                    }
                    var next = previous with { ItemId = op.Op == "copy" ? ids.NewId() : previous.ItemId,
                        LogicalPath = destination, Revision = op.Op == "copy" ? 1 : previous.Revision + 1,
                        CreatedAt = op.Op == "copy" ? time.GetUtcNow() : previous.CreatedAt, UpdatedAt = time.GetUtcNow() };
                    knownPaths.Add(destination); added.Add(Row(next, blob));
                }
            }
            if (rows.Count - removed.Length + added.Count > WorkspaceStructureLimits.MaxEntries
                || rows.Count(r => !FromRow(r).Directory) - removed.Count(r => !FromRow(r).Directory) + added.Count(r => !FromRow(r).Directory) > AgentWorkspaceLimits.MaxItems)
                throw AgentCoreErrors.WorkspaceQuotaExceeded();
            await CommitRowsAsync(owner, removed, added, ct);
        }
        catch { foreach (var blob in newBlobs) { DeleteFile(blob + ".partial"); DeleteFile(blob); } throw; }
        if (op.Op == "delete") foreach (var old in removed.Where(r => !FromRow(r).Directory)) DeleteFile(BlobPath(owner, old.BlobKey));

        void AddDirectory(string path)
        {
            // The shared plan supplies parents before children, including implicit legacy folders.
            if (knownPaths.Add(path)) added.Add(DirectoryRow(owner, path));
        }
    }

    private List<AgentWorkspaceRow> ParentRows(Guid owner, string path, IReadOnlyList<AgentWorkspaceRow> existing, Guid? sourceSessionId = null)
    {
        var result = new List<AgentWorkspaceRow>();
        var indexed = existing.ToDictionary(row => row.PathKey, StringComparer.Ordinal);
        for (var parent = path[..path.LastIndexOf('/')]; parent != "/home"; parent = parent[..parent.LastIndexOf('/')])
        {
            AgentHomePath.Normalize(parent);
            if (indexed.TryGetValue(parent.ToUpperInvariant(), out var old))
            {
                var item = FromRow(old);
                if (!item.Directory || item.LogicalPath != parent) throw AgentCoreErrors.Conflict("Home parent conflicts with an existing file or case-only path.");
                continue;
            }
            result.Add(DirectoryRow(owner, parent, sourceSessionId));
        }
        result.Reverse(); return result;
    }
    private AgentWorkspaceRow DirectoryRow(Guid owner, string path, Guid? sourceSessionId = null) => Row(new AgentWorkspaceItem(ids.NewId(), owner, path,
        "inode/directory", 0, Hash([]), 1, time.GetUtcNow(), time.GetUtcNow(), sourceSessionId, Directory: true), "");
    private static AgentWorkspaceRow Row(AgentWorkspaceItem item, string blob) => new()
    {
        ItemId = item.ItemId.ToString("D"), AgentInstanceId = item.AgentInstanceId.ToString("D"), PathKey = item.LogicalPath.ToUpperInvariant(),
        Revision = item.Revision, ByteSize = item.ByteSize, BlobKey = blob, MetadataJson = JsonSerializer.Serialize(item)
    };
    private static string TreeHash(IEnumerable<AgentWorkspaceRow> rows) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
        rows.OrderBy(r => r.PathKey, StringComparer.Ordinal).Select(r => new { r.ItemId, r.PathKey, r.Revision, r.ByteSize, r.BlobKey })))).ToLowerInvariant();

    private async ValueTask CommitRowsAsync(Guid owner, IReadOnlyList<AgentWorkspaceRow> removed, IReadOnlyList<AgentWorkspaceRow> added, CancellationToken ct)
    {
        if (contexts is null)
        {
            var current = _memory.GetOrAdd(owner, _ => []);
            var oldIds = removed.Select(r => r.ItemId).ToHashSet(StringComparer.Ordinal);
            current.RemoveAll(r => oldIds.Contains(r.ItemId)); current.AddRange(added); return;
        }
        await using var db = await contexts.CreateDbContextAsync(ct);
        var remaining = added.ToDictionary(r => r.ItemId, StringComparer.Ordinal);
        foreach (var old in removed)
        {
            db.Attach(old);
            if (remaining.Remove(old.ItemId, out var next)) db.Entry(old).CurrentValues.SetValues(next);
            else db.Remove(old);
        }
        db.AddRange(remaining.Values);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { throw AgentCoreErrors.Conflict("Home changed during metadata commit. Reload the current tree."); }
    }

    private static async Task WriteImmutableBlobAsync(string path, byte[] bytes, CancellationToken ct)
    {
        var temp = path + ".partial";
        await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
        {
            await stream.WriteAsync(bytes, ct); await stream.FlushAsync(ct); stream.Flush(true);
        }
        ct.ThrowIfCancellationRequested(); File.Move(temp, path);
    }
}
