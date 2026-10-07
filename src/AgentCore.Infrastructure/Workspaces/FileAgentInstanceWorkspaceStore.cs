using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Workspaces;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Workspaces;

// Logical names never enter blob paths. SQLite and the key-free InMemory profile share these exact semantics.
public sealed partial class FileAgentInstanceWorkspaceStore(
    string blobRoot, TimeProvider time, IIdGenerator ids,
    IDbContextFactory<AgentCoreDbContext>? contexts = null,
    long maxFileBytes = AgentWorkspaceLimits.MaxFileBytes,
    long maxInstanceBytes = AgentWorkspaceLimits.MaxInstanceBytes) : IAgentInstanceWorkspaceStore
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();
    private readonly ConcurrentDictionary<Guid, byte> _recovered = new();
    private readonly ConcurrentDictionary<Guid, List<AgentWorkspaceRow>> _memory = new();

    public async ValueTask<AgentWorkspacePage> ListAsync(Guid instanceId, string prefix, string? afterPath, int limit, CancellationToken cancellationToken = default)
    {
        prefix = AgentHomePath.Normalize(prefix, false);
        if (limit < 1 || limit > AgentWorkspaceLimits.MaxPageItems) throw AgentCoreErrors.Validation("Invalid home list bound.");
        return await WithAsync(instanceId, async ct =>
        {
            var all = await RowsAsync(instanceId, ct);
            var matches = all.Select(FromRow).Where(i => i.LogicalPath == prefix || i.LogicalPath.StartsWith(prefix + "/", StringComparison.Ordinal))
                .Where(i => afterPath is null || string.CompareOrdinal(i.LogicalPath, afterPath) > 0)
                .OrderBy(i => i.LogicalPath, StringComparer.Ordinal).Take(limit + 1).ToArray();
            return new AgentWorkspacePage(matches.Take(limit).ToArray(), all.Sum(r => r.ByteSize), all.Count,
                matches.Length > limit ? matches[limit - 1].LogicalPath : null, TreeHash(all));
        }, cancellationToken);
    }

    public ValueTask<AgentWorkspaceContent> ReadAsync(Guid instanceId, Guid? itemId, string? path, CancellationToken cancellationToken = default) =>
        WithAsync(instanceId, async ct =>
        {
            if (path is not null) path = AgentHomePath.Normalize(path);
            var row = (await RowsAsync(instanceId, ct)).SingleOrDefault(r => itemId.HasValue ? r.ItemId == itemId.Value.ToString("D") : FromRow(r).LogicalPath == path)
                ?? throw AgentCoreErrors.NotFound("Home file was not found.");
            var item = FromRow(row);
            if (item.Directory) throw AgentCoreErrors.Validation("Read requires a file; list the directory instead.");
            var physical = BlobPath(instanceId, row.BlobKey);
            if (!File.Exists(physical)) throw AgentCoreErrors.NotFound("Home content was not found.");
            if (new FileInfo(physical).Length != item.ByteSize || item.ByteSize > maxFileBytes) throw AgentCoreErrors.Conflict("Home content integrity check failed.");
            var bytes = await File.ReadAllBytesAsync(physical, ct);
            if (Hash(bytes) != item.Sha256Hex) throw AgentCoreErrors.Conflict("Home content integrity check failed.");
            return new AgentWorkspaceContent(item, bytes);
        }, cancellationToken);

    public ValueTask<AgentWorkspaceItem> WriteFileAsync(Guid instanceId, string path, string contentType, ReadOnlyMemory<byte> bytes,
        Guid? sourceSessionId, long? expectedRevision, string? expectedSha256, CancellationToken cancellationToken = default) =>
        WithAsync(instanceId, async ct =>
        {
            path = AgentHomePath.Normalize(path);
            var rows = await RowsAsync(instanceId, ct);
            var key = path.ToUpperInvariant();
            var old = rows.SingleOrDefault(r => r.PathKey == key);
            var previous = old is null ? null : FromRow(old);
            if (previous?.Directory == true) throw AgentCoreErrors.Conflict("A directory occupies the retained file path.");
            if (previous is not null && (previous.LogicalPath != path
                || expectedRevision is null && expectedSha256 is null
                || expectedRevision.HasValue && expectedRevision != previous.Revision
                || expectedSha256 is not null && expectedSha256 != previous.Sha256Hex))
                throw AgentCoreErrors.Conflict($"Home file changed. Current revision: {previous.Revision}; SHA-256: {previous.Sha256Hex}.");
            if (previous is null && (expectedRevision.HasValue || expectedSha256 is not null))
                throw AgentCoreErrors.Conflict("Expected home file no longer exists.");
            // Reject file/directory ambiguity and case-only directory collisions on every supported OS.
            foreach (var row in rows.Where(r => r != old))
            {
                var other = FromRow(row).LogicalPath;
                if (other.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith(other + "/", StringComparison.OrdinalIgnoreCase) && !FromRow(row).Directory)
                    throw AgentCoreErrors.Conflict("Home file conflicts with an existing directory or file.");
                var a = path.Split('/'); var b = other.Split('/');
                for (var n = 0; n < Math.Min(a.Length, b.Length) - 1; n++)
                {
                    if (!a[n].Equals(b[n], StringComparison.OrdinalIgnoreCase)) break;
                    if (a[n] != b[n]) throw AgentCoreErrors.Conflict("Case-only home directory collisions are denied.");
                }
            }
            var parents = ParentRows(instanceId, path, rows, sourceSessionId);
            if (bytes.Length > maxFileBytes || rows.Sum(r => r.ByteSize) - (previous?.ByteSize ?? 0) + bytes.Length > maxInstanceBytes
                || previous is null && rows.Count(r => !FromRow(r).Directory) >= AgentWorkspaceLimits.MaxItems
                || rows.Count + parents.Count + (previous is null ? 1 : 0) > WorkspaceStructureLimits.MaxEntries)
            {
                OperationalDiagnostics.RecordResourceLimit("agentWorkspace");
                throw AgentCoreErrors.WorkspaceQuotaExceeded();
            }
            var oldBlob = old?.BlobKey;
            var now = time.GetUtcNow();
            var item = new AgentWorkspaceItem(previous?.ItemId ?? ids.NewId(), instanceId, path,
                string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
                bytes.Length, Hash(bytes.Span), (previous?.Revision ?? 0) + 1, previous?.CreatedAt ?? now, now, sourceSessionId);
            var blob = ids.NewId().ToString("N");
            var physical = BlobPath(instanceId, blob);
            Directory.CreateDirectory(Path.GetDirectoryName(physical)!);
            physical = BlobPath(instanceId, blob); // recheck newly created ancestors
            var temp = physical + ".partial";
            try
            {
                await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                {
                    await stream.WriteAsync(bytes, ct);
                    await stream.FlushAsync(ct);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temp, physical);
                var next = new AgentWorkspaceRow { ItemId = item.ItemId.ToString("D"), AgentInstanceId = instanceId.ToString("D"), PathKey = key,
                    ByteSize = item.ByteSize, Revision = item.Revision, BlobKey = blob, MetadataJson = JsonSerializer.Serialize(item) };
                await CommitRowsAsync(instanceId, old is null ? [] : [old], [..parents, next], ct);
            }
            catch
            {
                DeleteFile(temp);
                DeleteFile(physical);
                throw;
            }
            // Commit points only at immutable complete bytes; leftover superseded blobs are unreachable and recovered below.
            if (oldBlob is not null) DeleteFile(BlobPath(instanceId, oldBlob));
            return item;
        }, cancellationToken);

    public ValueTask DeleteAsync(Guid instanceId, Guid itemId, long expectedRevision, CancellationToken cancellationToken = default) =>
        WithAsync(instanceId, async ct =>
        {
            var rows = await RowsAsync(instanceId, ct);
            var old = rows.SingleOrDefault(r => r.ItemId == itemId.ToString("D"))
                ?? throw AgentCoreErrors.NotFound("Home file was not found.");
            if (old.Revision != expectedRevision) throw AgentCoreErrors.Conflict("Home file revision is stale. Reload before deleting.");
            var item = FromRow(old);
            if (item.Directory && rows.Any(r => FromRow(r).LogicalPath.StartsWith(item.LogicalPath + "/", StringComparison.Ordinal)))
                throw AgentCoreErrors.Conflict("Non-empty directory deletion requires an explicit recursive filesystem action.");
            var physical = item.Directory ? null : BlobPath(instanceId, old.BlobKey);
            await CommitAsync(instanceId, old, null, ct);
            if (physical is not null) DeleteFile(physical);
            return 0;
        }, cancellationToken).AsVoid();

    public ValueTask DeleteInstanceContentAsync(Guid instanceId, CancellationToken cancellationToken = default) =>
        WithAsync(instanceId, ct =>
        {
            ct.ThrowIfCancellationRequested();
            var directory = OwnerDirectory(instanceId);
            if (Directory.Exists(directory))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory)) DenyLinks(entry);
                Directory.Delete(directory, recursive: true);
            }
            return ValueTask.FromResult(0);
        }, cancellationToken).AsVoid();

    public ValueTask DeleteInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) =>
        WithAsync(instanceId, async ct =>
        {
            var directory = OwnerDirectory(instanceId);
            if (contexts is not null)
            {
                await using var db = await contexts.CreateDbContextAsync(ct);
                await db.AgentWorkspaceItems.Where(r => r.AgentInstanceId == instanceId.ToString("D")).ExecuteDeleteAsync(ct);
            }
            _memory.TryRemove(instanceId, out _);
            if (Directory.Exists(directory))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory)) DenyLinks(entry);
                Directory.Delete(directory, recursive: true);
            }
            return 0;
        }, cancellationToken).AsVoid();

    private async ValueTask<List<AgentWorkspaceRow>> RowsAsync(Guid owner, CancellationToken ct)
    {
        if (contexts is null) return _memory.GetOrAdd(owner, _ => []);
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await db.AgentWorkspaceItems.AsNoTracking().Where(r => r.AgentInstanceId == owner.ToString("D")).ToListAsync(ct);
        if (_recovered.TryAdd(owner, 0))
        {
            var directory = OwnerDirectory(owner);
            if (Directory.Exists(directory))
            {
                var reachable = rows.Select(r => r.BlobKey).ToHashSet(StringComparer.Ordinal);
                foreach (var file in Directory.EnumerateFiles(directory).Take(AgentWorkspaceLimits.MaxItems * 2))
                {
                    ct.ThrowIfCancellationRequested(); DenyLinks(file);
                    var name = Path.GetFileName(file);
                    var key = name.EndsWith(".partial", StringComparison.Ordinal) ? name[..^8] : name;
                    if (Guid.TryParseExact(key, "N", out _) && !reachable.Contains(name)) DeleteFile(file);
                }
            }
        }
        return rows;
    }

    private async ValueTask CommitAsync(Guid owner, AgentWorkspaceRow? old, AgentWorkspaceRow? next, CancellationToken ct)
    {
        if (contexts is null)
        {
            var rows = _memory.GetOrAdd(owner, _ => []);
            if (old is not null) rows.Remove(old);
            if (next is not null) rows.Add(next);
            return;
        }
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (old is not null)
        {
            db.Attach(old);
            db.AgentWorkspaceItems.Remove(old);
            // Delete and insert share a transaction; stable id and revision remain the public identity.
        }
        if (next is not null)
        {
            if (old is not null)
            {
                db.Entry(old).State = EntityState.Modified;
                old.PathKey = next.PathKey; old.ByteSize = next.ByteSize; old.Revision = next.Revision;
                old.BlobKey = next.BlobKey; old.MetadataJson = next.MetadataJson;
            }
            else db.AgentWorkspaceItems.Add(next);
        }
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { throw AgentCoreErrors.Conflict("Home changed during commit. Reload current metadata."); }
    }

    private async ValueTask<T> WithAsync<T>(Guid owner, Func<CancellationToken, ValueTask<T>> action, CancellationToken ct)
    {
        var gate = _gates.GetOrAdd(owner, _ => new(1, 1));
        await gate.WaitAsync(ct);
        try { return await action(ct); }
        finally { gate.Release(); }
    }

    private string OwnerDirectory(Guid owner)
    {
        AttachmentBlobKeys.EnsureSafeRoot(blobRoot);
        var directory = Path.Combine(Path.GetFullPath(blobRoot), owner.ToString("N"));
        DenyLinks(directory);
        return directory;
    }

    private string BlobPath(Guid owner, string blob)
    {
        if (blob.Length != 32 || !Guid.TryParseExact(blob, "N", out _)) throw AgentCoreErrors.Validation("Invalid home content key.");
        var path = Path.Combine(OwnerDirectory(owner), blob);
        DenyLinks(path);
        return path;
    }

    private void DenyLinks(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if (File.Exists(current) || Directory.Exists(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw AgentCoreErrors.Forbidden("Linked home storage paths are denied.");
            if (current == Path.GetFullPath(blobRoot)) break;
        }
    }

    private static void DeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* Unreachable blob; never authorize from its presence. */ }
    }
    private static AgentWorkspaceItem FromRow(AgentWorkspaceRow row) => JsonSerializer.Deserialize<AgentWorkspaceItem>(row.MetadataJson)!;
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

internal static class WorkspaceValueTasks
{
    public static async ValueTask AsVoid<T>(this ValueTask<T> task) { await task; }
}
