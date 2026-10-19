using System.Runtime.InteropServices;
using System.Text;
using AgentCore.Application.Sessions;
using Microsoft.Win32.SafeHandles;

namespace AgentCore.Infrastructure.Workspaces;

/// <summary>
/// Ordinary files, accessed relative to a trusted owner directory. No file index,
/// snapshot or revision is involved. All descendant opens refuse symlink traversal;
/// links are resolved explicitly in the logical scope, never in the host namespace.
/// </summary>
internal sealed partial class CanonicalWorkspaceFilesystem
{
    internal sealed record Entry(string Path, string Kind, long ByteSize, DateTimeOffset ModifiedAt,
        uint Mode, ulong Device, ulong Inode, ulong Links, string? LinkTarget = null, uint Uid = 0, uint Gid = 0)
    {
        internal bool Directory => Kind == "directory";
    }

    internal sealed record Page(IReadOnlyList<Entry> Entries, string? NextPath, bool Truncated);
    internal sealed record Usage(long Bytes, long Entries, string Enforcement = "sampled");

    private readonly string _root;
    private readonly string _logicalRoot;
    internal string PhysicalRoot => _root;
    internal Action<string>? BeforeOpen { get; set; }
    private const int ScanBudget = 1_000_000;

    internal CanonicalWorkspaceFilesystem(string physicalRoot, string logicalRoot)
    {
        if (logicalRoot is not "/home" and not "/working") throw new ArgumentException("Invalid workspace scope.");
        _root = Path.GetFullPath(physicalRoot);
        if (_root == Path.GetPathRoot(_root)) throw new ArgumentException("A dedicated owner root is required.");
        _logicalRoot = logicalRoot;
    }

    internal void Ensure()
    {
        using var root = OpenRoot(create: true);
    }

    // Reconcile only this exact managed root through its no-follow descriptor.
    // Same-owner concurrent Sessions converge on 0700 without touching contents.
    internal void PrepareManagedRoot(uint uid, uint gid)
    {
        using var root = OpenRoot(create: true);
        var node = Metadata(Fd(root));
        if (uid == 0 || node.Uid != uid || node.Gid != gid)
            throw AgentCoreErrors.Forbidden("Managed workspace ownership could not be verified; canonical files were retained.");
        if ((node.Mode & 0xfff) != 0x1c0 && Chmod(Fd(root), 0x1c0) != 0)
            ThrowIo("Managed workspace permissions could not be prepared.");
    }

    private static readonly object LayoutGate = new();
    internal void EnsureLayout()
    {
        lock (LayoutGate)
        {
            using var root = OpenRoot(create: true);
            const string marker = ".canonical-layout";
            const string format = "canonical-filesystem-1\n";
            var existing = LStat(root, marker);
            if (existing is not null)
            {
                using var handle = OpenChild(root, marker);
                var node = Metadata(Fd(handle));
                if (node.Kind != FileKind || node.Links != 1 || node.Size != Encoding.UTF8.GetByteCount(format)) ResetRequired();
                using var stream = new FileStream(handle, FileAccess.Read);
                var bytes = new byte[Encoding.UTF8.GetByteCount(format)];
                stream.ReadExactly(bytes);
                if (Encoding.UTF8.GetString(bytes) != format) ResetRequired();
                return;
            }
            if (Names(root).Count != 0) ResetRequired();
            using var created = OpenChild(root, marker, write: true);
            using var output = new FileStream(created, FileAccess.Write);
            output.Write(Encoding.UTF8.GetBytes(format));
            output.Flush(flushToDisk: true);
        }
        static void ResetRequired() => throw AgentCoreErrors.Persistence(
            "data_reset_required: Legacy workspace layout/data reset required. Stop the API and explicitly back up/reset the database and data roots. No existing data was moved or deleted.");
    }

    internal Entry Stat(string path) => Stat(path, false);
    internal Entry StatResolved(string path) => Stat(path, true);
    private Entry Stat(string path, bool resolveLinks)
    {
        using var root = OpenRoot();
        var relative = resolveLinks ? Resolve(root, Relative(path)) : Relative(path);
        if (relative.Length == 0) return Describe(path, Metadata(Fd(root)));
        using var parent = Parent(root, relative);
        var name = Leaf(relative);
        var node = LStat(parent, name) ?? throw AgentCoreErrors.NotFound("Workspace path was not found.");
        return Describe(path, node, node.Kind == LinkKind ? ReadLink(parent, name) : null);
    }

    internal void RequireDirectory(string path)
    {
        using var root = OpenRoot();
        using var directory = Descend(root, Parts(Resolve(root, Relative(path))));
    }

    // Cursors are lexical observations. Concurrent renames can omit/repeat entries;
    // callers restart from the first page to refresh. No stable snapshot is implied.
    internal Page List(string path, string? afterPath = null, int limit = 256) => ListCore(path, afterPath, limit, true);
    internal Page ListUnlinked(string path, string? afterPath = null, int limit = 256) => ListCore(path, afterPath, limit, false);
    private Page ListCore(string path, string? afterPath, int limit, bool resolveLinks)
    {
        if (limit is < 1 or > 4096) throw AgentCoreErrors.Validation("Invalid workspace page size.");
        using var root = OpenRoot();
        var relative = resolveLinks ? Resolve(root, Relative(path)) : Relative(path);
        using var directory = Descend(root, Parts(relative));
        var entries = new List<Entry>();
        foreach (var name in Names(directory).Order(StringComparer.Ordinal))
        {
            var logical = path.TrimEnd('/') + "/" + name;
            if (afterPath is not null && string.CompareOrdinal(logical, afterPath) <= 0) continue;
            var node = LStat(directory, name);
            if (node is null) continue; // Ordinary concurrent deletion.
            entries.Add(Describe(logical, node, node.Kind == LinkKind ? ReadLink(directory, name) : null));
            if (entries.Count > limit) return new(entries.Take(limit).ToArray(), entries[limit - 1].Path, true);
        }
        return new(entries, null, false);
    }

    internal ValueTask<byte[]> ReadAsync(string path, long maxBytes, CancellationToken ct = default) => ReadFileAsync(path, maxBytes, true, ct);
    internal ValueTask<byte[]> ReadUnlinkedAsync(string path, long maxBytes, CancellationToken ct = default) => ReadFileAsync(path, maxBytes, false, ct);
    private async ValueTask<byte[]> ReadFileAsync(string path, long maxBytes, bool resolveLinks, CancellationToken ct)
    {
        using var root = OpenRoot();
        var relative = resolveLinks ? Resolve(root, Relative(path)) : Relative(path);
        using var parent = Parent(root, relative);
        var name = Leaf(relative);
        BeforeOpen?.Invoke(path);
        using var handle = OpenChild(parent, name);
        var node = Metadata(Fd(handle));
        if (node.Kind != FileKind) throw AgentCoreErrors.Validation("Workspace read requires an ordinary file.");
        VerifyHardLinks(root, node, ct);
        if (node.Size < 0 || node.Size > maxBytes || node.Size > int.MaxValue) throw AgentCoreErrors.WorkspaceQuotaExceeded();
        using var stream = new FileStream(handle, FileAccess.Read);
        // Bound reads even when a concurrent process grows the opened inode.
        var bytes = new byte[checked((int)node.Size)];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(count), ct);
            if (read == 0) break;
            count += read;
        }
        return count == bytes.Length ? bytes : bytes[..count];
    }

    internal async ValueTask WriteAsync(string path, ReadOnlyMemory<byte> bytes, long maxFileBytes,
        long maxRootBytes, CancellationToken ct = default, Func<CancellationToken, ValueTask>? authorizeCommit = null)
    {
        if (bytes.Length > maxFileBytes) throw AgentCoreErrors.WorkspaceQuotaExceeded();
        using var root = OpenRoot(create: true);
        var relative = Resolve(root, Relative(path), allowMissing: true);
        if (relative.Length == 0) throw AgentCoreErrors.Forbidden("Workspace roots cannot be replaced.");
        using var parent = Parent(root, relative, create: true);
        var name = Leaf(relative);
        var old = LStat(parent, name);
        if (old is not null && old.Kind != FileKind) throw AgentCoreErrors.Conflict("Workspace destination is not an ordinary file.");
        if (old is not null) VerifyHardLinks(root, old, ct);
        var usage = Measure(root, ct);
        if (usage.Bytes - (old is { Links: <= 1 } ? old.Size : 0) + bytes.Length > maxRootBytes) throw AgentCoreErrors.WorkspaceQuotaExceeded();
        var temporary = "." + Guid.NewGuid().ToString("N") + ".write";
        try
        {
            using (var handle = OpenChild(parent, temporary, write: true))
            {
                if (old is not null && Chmod(Fd(handle), (int)(old.Mode & 0x1ff)) != 0)
                    throw new IOException("Workspace file permissions could not be preserved.");
                using var stream = new FileStream(handle, FileAccess.Write);
                await stream.WriteAsync(bytes, ct);
                await stream.FlushAsync(ct);
            }
            ct.ThrowIfCancellationRequested();
            if (authorizeCommit is not null) await authorizeCommit(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            // Replaces the name atomically, deliberately breaking any old hard-link
            // association like an ordinary editor. Concurrent last rename wins.
            if (RenameAt(Fd(parent), temporary, Fd(parent), name) != 0) ThrowIo("Workspace replacement failed.");
        }
        finally { UnlinkAt(Fd(parent), temporary, 0); }
    }

    internal async ValueTask<bool> TryWriteNewAsync(string path, ReadOnlyMemory<byte> bytes, long maxFileBytes,
        long maxRootBytes, CancellationToken ct = default, Func<CancellationToken, ValueTask>? authorizeCommit = null)
    {
        if (bytes.Length > maxFileBytes) throw AgentCoreErrors.WorkspaceQuotaExceeded();
        using var root = OpenRoot(create: true);
        // Resolve only the parent. An occupied leaf, including a symlink, must never be followed.
        var relative = Relative(path);
        if (relative.Length == 0) throw AgentCoreErrors.Forbidden("Workspace roots cannot be replaced.");
        var parts = Parts(relative);
        var resolvedParent = Resolve(root, string.Join('/', parts.SkipLast(1)), allowMissing: true);
        using var parent = Descend(root, Parts(resolvedParent), create: true);
        var name = Leaf(relative);
        if (LStat(parent, name) is not null) return false;
        if (Measure(root, ct).Bytes + bytes.Length > maxRootBytes) throw AgentCoreErrors.WorkspaceQuotaExceeded();
        var temporary = "." + Guid.NewGuid().ToString("N") + ".write";
        try
        {
            using (var handle = OpenChild(parent, temporary, write: true))
            {
                using var stream = new FileStream(handle, FileAccess.Write);
                await stream.WriteAsync(bytes, ct);
                await stream.FlushAsync(ct);
            }
            ct.ThrowIfCancellationRequested();
            if (authorizeCommit is not null) await authorizeCommit(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            BeforeOpen?.Invoke(path); // Deterministic external-writer race coverage.
            if (RenameNoReplace(parent, temporary, parent, name) == 0) return true;
            if (Marshal.GetLastPInvokeError() == 17) return false; // EEXIST, for any occupied name.
            ThrowIo("Workspace exclusive creation failed.");
            return false;
        }
        finally { UnlinkAt(Fd(parent), temporary, 0); }
    }

    internal void Mkdir(string path)
    {
        using var root = OpenRoot(create: true);
        var relative = Relative(path);
        using var directory = Descend(root, Parts(relative), create: true);
    }

    internal void Move(string source, string destination)
    {
        using var root = OpenRoot();
        var from = Relative(source); var to = Relative(destination);
        ProtectRoot(from); ProtectRoot(to);
        if (to.StartsWith(from + "/", StringComparison.Ordinal)) throw AgentCoreErrors.Validation("Cannot move a directory into itself.");
        using var sourceParent = Parent(root, from);
        using var destinationParent = Parent(root, to, create: true);
        if (LStat(sourceParent, Leaf(from)) is null) throw AgentCoreErrors.NotFound("Workspace source was not found.");
        if (LStat(destinationParent, Leaf(to)) is not null) throw AgentCoreErrors.Conflict("Workspace destination already exists.");
        // The ordinary rename is atomic within this filesystem; no multi-step CAS
        // or cross-root move is exposed. Concurrent writers have OS semantics.
        if (RenameNoReplace(sourceParent, Leaf(from), destinationParent, Leaf(to)) != 0) ThrowIo("Workspace rename failed; the destination may already exist.");
    }

    internal void Delete(string path, bool recursive, CancellationToken ct = default)
    {
        using var root = OpenRoot();
        var relative = Relative(path); ProtectRoot(relative);
        using var parent = Parent(root, relative);
        Remove(parent, Leaf(relative), recursive, ct);
    }
    internal async ValueTask DeleteAuthorizedAsync(string path, Func<CancellationToken, ValueTask> authorizeCommit, CancellationToken ct)
    {
        using var root = OpenRoot();
        var relative = Relative(path); ProtectRoot(relative);
        using var parent = Parent(root, relative);
        await authorizeCommit(ct).ConfigureAwait(false); ct.ThrowIfCancellationRequested();
        Remove(parent, Leaf(relative), recursive: false, ct);
    }

    internal void CopyTo(string source, CanonicalWorkspaceFilesystem destinationFilesystem, string destination, CancellationToken ct = default,
        long maxFileBytes = long.MaxValue, long maxRootBytes = long.MaxValue) =>
        CopyToCoreAsync(source, destinationFilesystem, destination, null, ct, maxFileBytes, maxRootBytes).GetAwaiter().GetResult();

    internal ValueTask CopyToAuthorizedAsync(string source, CanonicalWorkspaceFilesystem destinationFilesystem, string destination,
        Func<CancellationToken, ValueTask> authorizeCommit, CancellationToken ct, long maxFileBytes, long maxRootBytes) =>
        CopyToCoreAsync(source, destinationFilesystem, destination, authorizeCommit ?? throw new ArgumentNullException(nameof(authorizeCommit)), ct, maxFileBytes, maxRootBytes);

    private async ValueTask CopyToCoreAsync(string source, CanonicalWorkspaceFilesystem destinationFilesystem, string destination,
        Func<CancellationToken, ValueTask>? authorizeCommit, CancellationToken ct, long maxFileBytes, long maxRootBytes)
    {
        // The first check also protects destination-root/parent creation. Copy is
        // one coarse tree operation; cancellation may retain a partial tree.
        if (authorizeCommit is not null) await authorizeCommit(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        using var root = OpenRoot(); using var destinationRoot = destinationFilesystem.OpenRoot(create: true);
        var from = Relative(source); var to = destinationFilesystem.Relative(destination);
        ProtectRoot(from); ProtectRoot(to);
        if (_root == destinationFilesystem._root && (to == from || to.StartsWith(from + "/", StringComparison.Ordinal)))
            throw AgentCoreErrors.Validation("Cannot copy a directory into itself.");
        using var sourceParent = Parent(root, from);
        using var destinationParent = Parent(destinationRoot, to, create: true);
        if (LStat(destinationParent, Leaf(to)) is not null) throw AgentCoreErrors.Conflict("Workspace destination already exists.");
        var count = 0;
        var remaining = maxRootBytes - Measure(destinationRoot, ct).Bytes;
        if (authorizeCommit is not null) await authorizeCommit(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        CopyEntry(root, sourceParent, Leaf(from), destinationParent, Leaf(to), ref count, ref remaining, maxFileBytes, ct);
    }

    internal Usage Measure(CancellationToken ct = default)
    {
        using var root = OpenRoot(); return Measure(root, ct);
    }

    internal void ValidateHardLinkOwnership(CancellationToken ct = default)
    {
        using var root = OpenRoot();
        var counts = new Dictionary<(ulong, ulong), (ulong Expected, ulong Found)>();
        var scanned = 0;
        void Walk(SafeFileHandle directory, int depth)
        {
            if (depth > 256) throw AgentCoreErrors.Forbidden("Hard-link ownership scan exceeded its budget.");
            foreach (var name in Names(directory))
            {
                ct.ThrowIfCancellationRequested();
                if (++scanned > ScanBudget) throw AgentCoreErrors.Forbidden("Hard-link ownership scan exceeded its budget.");
                var node = LStat(directory, name);
                if (node is null) continue;
                if (node.Kind == DirectoryKind) { using var child = OpenChild(directory, name, directory: true); Walk(child, depth + 1); }
                else if (node.Kind == FileKind && node.Links > 1)
                {
                    var key = (node.Device, node.Inode);
                    var count = counts.GetValueOrDefault(key);
                    if (count.Expected != 0 && count.Expected != node.Links) throw AgentCoreErrors.Conflict("Hard-link ownership changed during inspection.");
                    counts[key] = (node.Links, count.Found + 1);
                }
            }
        }
        Walk(root, 0);
        if (counts.Values.Any(count => count.Expected != count.Found))
            throw AgentCoreErrors.Forbidden("Cross-root hard-link aliases prevent safe workspace mounts.");
    }

    private static Usage Measure(SafeFileHandle root, CancellationToken ct)
    {
        long total = 0; long count = 0;
        var inodes = new HashSet<(ulong, ulong)>();
        var links = new Dictionary<(ulong, ulong), (ulong Expected, ulong Found)>();
        void Walk(SafeFileHandle directory, int depth)
        {
            if (depth > 256) throw AgentCoreErrors.Validation("Workspace usage is unknown: scan budget exceeded.");
            foreach (var name in Names(directory))
            {
                ct.ThrowIfCancellationRequested();
                if (++count > ScanBudget) throw AgentCoreErrors.Validation("Workspace usage is unknown: scan budget exceeded.");
                var node = LStat(directory, name);
                if (node is null) continue;
                if (node.Kind == DirectoryKind)
                {
                    using var child = OpenChild(directory, name, directory: true);
                    Walk(child, depth + 1);
                }
                else if (node.Kind == FileKind)
                {
                    var key = (node.Device, node.Inode);
                    if (inodes.Add(key)) total = checked(total + Math.Max(0, node.Size));
                    if (node.Links > 1)
                    {
                        var observed = links.GetValueOrDefault(key);
                        if (observed.Expected != 0 && observed.Expected != node.Links)
                            throw AgentCoreErrors.Conflict("Hard-link ownership changed during usage sampling.");
                        links[key] = (node.Links, observed.Found + 1);
                    }
                }
            }
        }
        Walk(root, 0);
        if (links.Values.Any(link => link.Expected != link.Found))
            throw AgentCoreErrors.Forbidden("Cross-root hard-link aliases prevent safe workspace usage sampling.");
        return new(total, count);
    }

    private static void CopyEntry(SafeFileHandle scope, SafeFileHandle source, string from, SafeFileHandle destination, string to,
        ref int count, ref long remaining, long maxFileBytes, CancellationToken ct, int depth = 0)
    {
        ct.ThrowIfCancellationRequested();
        if (++count > ScanBudget || depth > 256) throw AgentCoreErrors.Validation("Workspace copy scan budget exceeded; partial files may remain.");
        var node = LStat(source, from) ?? throw AgentCoreErrors.NotFound("Workspace source was not found.");
        if (node.Kind == DirectoryKind)
        {
            MakeDirectory(destination, to, exclusive: true);
            using var sourceDirectory = OpenChild(source, from, directory: true);
            using var destinationDirectory = OpenChild(destination, to, directory: true);
            foreach (var name in Names(sourceDirectory)) CopyEntry(scope, sourceDirectory, name, destinationDirectory, name, ref count, ref remaining, maxFileBytes, ct, depth + 1);
            if (Chmod(Fd(destinationDirectory), (int)(node.Mode & 0x1ff)) != 0) ThrowIo("Workspace copy permissions failed.");
        }
        else if (node.Kind == LinkKind)
        {
            var target = ReadLink(source, from);
            // Preserves link identity without dereferencing it. Subsequent reads
            // independently authorize the target in the destination scope.
            if (SymlinkAt(target, Fd(destination), to) != 0) ThrowIo("Workspace link copy failed.");
        }
        else if (node.Kind == FileKind)
        {
            VerifyHardLinks(scope, node, ct);
            using var sourceHandle = OpenChild(source, from);
            using var destinationHandle = OpenChild(destination, to, write: true);
            var opened = Metadata(Fd(sourceHandle));
            if (opened.Kind != FileKind) throw AgentCoreErrors.Validation("Workspace source changed type.");
            VerifyHardLinks(scope, opened, ct);
            if (opened.Size < 0 || opened.Size > maxFileBytes || opened.Size > remaining) throw AgentCoreErrors.WorkspaceQuotaExceeded();
            remaining -= opened.Size;
            using var input = new FileStream(sourceHandle, FileAccess.Read);
            using var output = new FileStream(destinationHandle, FileAccess.Write);
            var buffer = new byte[65536]; long copied = 0;
            while (copied < opened.Size)
            {
                ct.ThrowIfCancellationRequested();
                var length = input.Read(buffer, 0, (int)Math.Min(buffer.Length, opened.Size - copied));
                if (length == 0) break;
                output.Write(buffer, 0, length); copied += length;
            }
            if (Chmod(Fd(destinationHandle), (int)(node.Mode & 0x1ff)) != 0) ThrowIo("Workspace copy permissions failed.");
        }
        else throw AgentCoreErrors.Validation("Workspace copy does not export special files; partial files may remain.");
    }

    private static void VerifyHardLinks(SafeFileHandle root, Node file, CancellationToken ct)
    {
        if (file.Links <= 1) return;
        ulong found = 0; var scanned = 0;
        void Walk(SafeFileHandle directory, int depth)
        {
            if (depth > 256) throw AgentCoreErrors.Forbidden("Hard-link ownership could not be verified.");
            foreach (var name in Names(directory))
            {
                ct.ThrowIfCancellationRequested();
                if (++scanned > ScanBudget) throw AgentCoreErrors.Forbidden("Hard-link ownership could not be verified.");
                var node = LStat(directory, name);
                if (node is null) continue;
                if (node.Kind == DirectoryKind) { using var child = OpenChild(directory, name, directory: true); Walk(child, depth + 1); }
                else if (node.Kind == FileKind && node.Device == file.Device && node.Inode == file.Inode) found++;
            }
        }
        Walk(root, 0);
        if (found != file.Links) throw AgentCoreErrors.Forbidden("Cross-root hard-link aliases are not authorized.");
    }

    private static void Remove(SafeFileHandle parent, string name, bool recursive, CancellationToken ct, int depth = 0)
    {
        ct.ThrowIfCancellationRequested();
        if (depth > 256) throw AgentCoreErrors.Validation("Workspace delete scan budget exceeded; partial deletion may have occurred.");
        var node = LStat(parent, name) ?? throw AgentCoreErrors.NotFound("Workspace path was not found.");
        if (node.Kind == DirectoryKind && recursive)
        {
            using var directory = OpenChild(parent, name, directory: true);
            foreach (var child in Names(directory)) Remove(directory, child, true, ct, depth + 1);
            if (!SameIdentity(node, LStat(parent, name))) throw AgentCoreErrors.Conflict("Workspace directory changed during deletion.");
        }
        if (UnlinkAt(Fd(parent), name, node.Kind == DirectoryKind ? RemoveDirectoryFlag : 0) != 0)
            ThrowIo("Workspace deletion failed; nonempty directories require recursive intent.");
    }

    private string Relative(string path)
    {
        if (path == _logicalRoot) return "";
        if (!path.StartsWith(_logicalRoot + "/", StringComparison.Ordinal)) throw AgentCoreErrors.Forbidden("Path is outside the authorized workspace root.");
        var relative = path[(_logicalRoot.Length + 1)..];
        if (Encoding.UTF8.GetByteCount(path) > 4095 || Parts(relative).Any(p => p is "" or "." or ".." || p.Contains('\0') || p.Contains('\\') || Encoding.UTF8.GetByteCount(p) > 255))
            throw AgentCoreErrors.Validation("Workspace path is invalid.");
        return relative;
    }

    private string Resolve(SafeFileHandle root, string relative, bool allowMissing = false, Action<string, string>? observedLink = null)
    {
        var parts = Parts(relative).ToList(); var links = 0;
        for (var index = 0; index < parts.Count; index++)
        {
            using var parent = Descend(root, parts.Take(index));
            var node = LStat(parent, parts[index]);
            if (node is null)
            {
                if (allowMissing) return string.Join('/', parts);
                throw AgentCoreErrors.NotFound("Workspace path was not found.");
            }
            if (node.Kind != LinkKind) continue;
            if (++links > 40) throw AgentCoreErrors.Validation("unsafe_link_target: symbolic-link cycle or excessive traversal.");
            var target = ReadLink(parent, parts[index]);
            observedLink?.Invoke(_logicalRoot + "/" + string.Join('/', parts.Take(index + 1)), target);
            List<string> resolved;
            if (target.StartsWith('/'))
            {
                if (target != _logicalRoot && !target.StartsWith(_logicalRoot + "/", StringComparison.Ordinal))
                    throw AgentCoreErrors.Forbidden("unsafe_link_target: link leaves the authorized logical root.");
                resolved = [];
                target = target[_logicalRoot.Length..].TrimStart('/');
            }
            else resolved = parts.Take(index).ToList();
            foreach (var part in target.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (part == ".") continue;
                if (part == "..")
                {
                    if (resolved.Count == 0) throw AgentCoreErrors.Forbidden("unsafe_link_target: link escapes the authorized workspace root.");
                    resolved.RemoveAt(resolved.Count - 1);
                }
                else resolved.Add(part);
            }
            parts = [..resolved, ..parts.Skip(index + 1)];
            Relative(_logicalRoot + (parts.Count == 0 ? "" : "/" + string.Join('/', parts)));
            index = -1;
        }
        return string.Join('/', parts);
    }

    private SafeFileHandle OpenRoot(bool create = false)
    {
        var path = _root;
        if (OperatingSystem.IsMacOS())
            foreach (var alias in new[] { "/var/", "/tmp/" })
                if (path.StartsWith(alias, StringComparison.Ordinal)) path = "/private" + path;
        var fd = Open("/", SafeFlags | DirectoryFlag);
        if (fd < 0) ThrowIo("Workspace filesystem root cannot be opened.");
        using var system = new SafeFileHandle((IntPtr)fd, true);
        return Descend(system, Parts(path.TrimStart('/')), create);
    }

    private static SafeFileHandle Parent(SafeFileHandle root, string relative, bool create = false)
    {
        ProtectRoot(relative); return Descend(root, Parts(relative)[..^1], create);
    }
    private static void ProtectRoot(string relative) { if (relative.Length == 0) throw AgentCoreErrors.Forbidden("Workspace root nodes are protected."); }
    private static string[] Parts(string relative) => relative.Length == 0 ? [] : relative.Split('/');
    private static string Leaf(string relative) => Parts(relative)[^1];
    private static Entry Describe(string path, Node node, string? target = null) => new(path,
        node.Kind switch { DirectoryKind => "directory", FileKind => "file", LinkKind => "symlink", _ => "special" },
        node.Kind == FileKind ? node.Size : 0, DateTimeOffset.FromUnixTimeSeconds(node.ModifiedSeconds).AddTicks(node.ModifiedNanos / 100),
        node.Mode & 0xfff, node.Device, node.Inode, node.Links, target, node.Uid, node.Gid);
}
