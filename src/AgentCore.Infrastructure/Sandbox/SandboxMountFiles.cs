using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using Microsoft.Win32.SafeHandles;
using OpenSandbox.Models;

namespace AgentCore.Infrastructure.Sandbox;

/// <summary>Private staging only. Never maps a canonical Workspace or an arbitrary owner-supplied path.</summary>
internal sealed class SandboxMountFiles(OpenSandboxOptions options)
{
    internal string Working(Guid generation) => Path.Combine(options.MountRoot(), generation.ToString("N"), "working");
    internal Action<string>? CollectionFault { get; set; }
    internal void ValidateConfiguration()
    {
        using var root = OpenRoot();
        var mode = Stat(Fd(root)).Mode;
        if ((mode & 0x12) != 0 && (mode & 0x200) == 0)
            throw AgentCoreErrors.Validation("Shared Sandbox staging root requires sticky permissions from the updated launcher.");
    }

    internal Volume Prepare(Guid generation)
    {
        var root = options.MountRoot();
        using var parent = OpenRoot(create: true);
        MakeDirectory(parent, generation.ToString("N"), 0x1c0, exclusive: true);
        using var directory = OpenChild(parent, generation.ToString("N"), directory: true);
        // Arbitrary image default users need write access only inside this mount.
        // The shared root is sticky (01777); generation parents remain 0700.
        // Native and packaged Core UIDs can coexist without deleting each other's generations.
        MakeDirectory(directory, "working", 0x1ff); // 0777 inside the private mount; arbitrary UIDs must replace hydrated files.
        return new Volume { Name = "working", Host = new Host { Path = Path.Combine(Path.GetFullPath(options.WorkspaceMountHostRoot ?? root), generation.ToString("N"), "working") }, MountPath = "/working", ReadOnly = false };
    }

    internal void Delete(Guid generation)
    {
        if (!Directory.Exists(options.MountRoot())) return;
        using var root = OpenRoot();
        var name = generation.ToString("N");
        if (TryStat(root, name) is null) return;
        Remove(root, name);
    }
    private static void Remove(SafeFileHandle parent, string name, int depth = 0)
    {
        if (depth > 64) throw new IOException("Sandbox staging cleanup depth exceeded; retained for inspection.");
        var info = TryStat(parent, name) ?? throw new IOException("Sandbox staging changed during cleanup.");
        if (info.Kind == 0x4000)
        {
            using var directory = OpenChild(parent, name, directory: true);
            if (!SameIdentity(info, Stat(Fd(directory)))) throw new IOException("Sandbox staging changed during cleanup.");
            for (var pass = 0; pass < 8; pass++)
            {
                var children = Names(directory, partial: true);
                foreach (var child in children) Remove(directory, child, depth + 1);
                if (children.Count < 8191) break;
                if (pass == 7) throw new IOException("Sandbox cleanup budget exhausted; another reconciliation pass is required.");
            }
            if (!SameIdentity(info, TryStat(parent, name))) throw new IOException("Sandbox staging changed during cleanup.");
        }
        if (UnlinkAt(Fd(parent), name, info.Kind == 0x4000 ? (OperatingSystem.IsMacOS() ? 0x80 : 0x200) : 0) != 0)
            throw new IOException("Sandbox staging cleanup failed.");
    }

    internal async Task HydrateAsync(Guid generation, WorkspaceTransfer transfer, CancellationToken ct)
    {
        using var root = OpenWorking(generation);
        foreach (var child in Names(root)) { ct.ThrowIfCancellationRequested(); Remove(root, child); }
        foreach (var entry in transfer.Entries.Skip(1).OrderBy(e => e.RelativePath.Count(c => c == '/')))
        {
            ValidatePath(entry.RelativePath);
            var parts = entry.RelativePath.Split('/');
            using var parent = Descend(root, parts[..^1], create: true);
            if (entry.Directory) MakeDirectory(parent, parts[^1], 0x1ff);
            else
            {
                using var file = OpenChild(parent, parts[^1], write: true);
                if (Chmod(Fd(file), 0x1b6) != 0) throw new IOException("Sandbox staging permissions could not be set.");
                using var stream = new FileStream(file, FileAccess.Write);
                await stream.WriteAsync(entry.Bytes, ct);
            }
        }
    }

    internal async Task<WorkspaceTransfer> CollectAsync(Guid generation, SandboxProfile profile, CancellationToken ct)
    {
        using var root = OpenWorking(generation);
        var first = await CaptureAsync(root, profile.StorageBytes, ct);
        using var secondRoot = OpenWorking(generation);
        if (!SameIdentity(Stat(Fd(root)), Stat(Fd(secondRoot)))) throw AgentCoreErrors.Conflict("Sandbox mount changed during collection.");
        var second = await CaptureAsync(secondRoot, profile.StorageBytes, ct);
        if (Fingerprint(first) != Fingerprint(second)) throw AgentCoreErrors.Conflict("Sandbox tree changed during collection.");
        return first;
    }
    private static string Fingerprint(WorkspaceTransfer transfer) => string.Join('\n', transfer.Entries.Select(e =>
        e.RelativePath + ":" + e.Directory + ":" + Convert.ToHexString(SHA256.HashData(e.Bytes))));

    private async Task<WorkspaceTransfer> CaptureAsync(SafeFileHandle root, long limit, CancellationToken ct)
    {
        var entries = new List<WorkspaceTransferEntry> { new("", true, "inode/directory", []) };
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0; var files = 0;
        async Task Walk(SafeFileHandle directory, string prefix)
        {
            var before = Stat(Fd(directory));
            foreach (var name in Names(directory).Order(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var relative = prefix + name; ValidatePath(relative);
                if (!paths.Add(relative) || paths.Count > 8191) throw AgentCoreErrors.Validation("Sandbox tree contains colliding or excess paths.");
                var info = TryStat(directory, name) ?? throw AgentCoreErrors.Conflict("Sandbox tree changed during collection.");
                CollectionFault?.Invoke(relative);
                if (info.Kind == 0x4000)
                {
                    using var child = OpenChild(directory, name, directory: true);
                    if (!SameIdentity(info, Stat(Fd(child)))) throw AgentCoreErrors.Conflict("Sandbox directory changed during collection.");
                    entries.Add(new(relative, true, "inode/directory", [])); await Walk(child, relative + "/");
                    if (!SameIdentity(info, TryStat(directory, name))) throw AgentCoreErrors.Conflict("Sandbox directory changed during collection.");
                }
                else
                {
                    if (info.Kind != 0x8000 || info.Links != 1) throw AgentCoreErrors.Validation("Sandbox links and special files cannot be committed.");
                    if (info.Size < 0 || info.Size > 50L * 1024 * 1024 || ++files > 4096 || (total += info.Size) > limit)
                        throw AgentCoreErrors.WorkspaceQuotaExceeded();
                    entries.Add(new(relative, false, "application/octet-stream", await ReadAsync(directory, name, info, ct)));
                }
            }
            if (Stat(Fd(directory)) != before) throw AgentCoreErrors.Conflict("Sandbox directory changed during collection.");
        }
        await Walk(root, ""); return new(entries);
    }

    // Sampled enforcement limits exposure, not an atomic filesystem quota. It never
    // reads file bytes and remains bounded even under a hostile workload tree.
    internal void CheckLiveUsage(Guid generation, long limit, CancellationToken ct)
    {
        using var root = OpenWorking(generation); long total = 0; var count = 0;
        void Walk(SafeFileHandle directory, int depth)
        {
            if (depth > 8) throw AgentCoreErrors.WorkspaceQuotaExceeded();
            foreach (var name in Names(directory))
            {
                ct.ThrowIfCancellationRequested();
                if (++count > 8191) throw AgentCoreErrors.WorkspaceQuotaExceeded();
                var info = TryStat(directory, name) ?? throw AgentCoreErrors.Conflict("Staging changed during quota inspection.");
                if (info.Kind == 0x4000)
                {
                    using var child = OpenChild(directory, name, directory: true);
                    if (!SameIdentity(info, Stat(Fd(child)))) throw AgentCoreErrors.Conflict("Staging changed during quota inspection.");
                    Walk(child, depth + 1);
                }
                else if (info.Kind == 0x8000)
                {
                    total = checked(total + info.Size);
                    if (info.Size < 0 || total > limit) throw AgentCoreErrors.WorkspaceQuotaExceeded();
                }
                // Links/special files are not followed. Collection rejects them.
            }
        }
        Walk(root, 0);
    }

    internal async Task ConfirmProbeAsync(Guid generation, CancellationToken ct)
    {
        using var root = OpenWorking(generation);
        const string name = ".agent-core-mount-probe";
        var info = TryStat(root, name) ?? throw AgentCoreErrors.Validation("Sandbox /working mount is not shared with Core. Check the platform mount configuration.");
        if (info.Kind != 0x8000 || info.Links != 1 || info.Size != 5 || Encoding.UTF8.GetString(await ReadAsync(root, name, info, ct)) != "ready")
            throw AgentCoreErrors.Validation("Sandbox /working mount is not shared with Core. Check the platform mount configuration.");
        if (UnlinkAt(Fd(root), name, 0) != 0) throw new IOException("Sandbox mount probe cleanup failed.");
    }

    private static void ValidatePath(string path)
    {
        if (path.Length is < 1 or > 480 || path.Contains('\\') || path.Any(char.IsControl) || path != path.Normalize(NormalizationForm.FormC)
            || path.Split('/').Length > 8 || path.Split('/').Any(p => p is "" or "." or ".." || p.StartsWith(".env", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".sock", StringComparison.OrdinalIgnoreCase)))
            throw AgentCoreErrors.Validation("Sandbox tree contains an invalid or protected path.");
    }
    private static async Task<byte[]> ReadAsync(SafeFileHandle parent, string name, Node expected, CancellationToken ct)
    {
        using var handle = OpenChild(parent, name);
        var fd = Fd(handle); var opened = Stat(fd);
        if (opened != expected || opened.Kind != 0x8000 || opened.Links != 1) throw AgentCoreErrors.Conflict("Sandbox file changed before collection.");
        using var stream = new FileStream(handle, FileAccess.Read);
        var bytes = new byte[checked((int)expected.Size)]; await stream.ReadExactlyAsync(bytes, ct);
        if (Stat(fd) != expected || TryStat(parent, name) != expected) throw AgentCoreErrors.Conflict("Sandbox file changed during collection.");
        return bytes;
    }
    private sealed record Node(uint Kind, ulong Inode, ulong Links, long Size, long ModifiedSeconds, long ModifiedNanos, ulong Device, long ChangedSeconds, long ChangedNanos, uint Mode);
    private static bool SameIdentity(Node a, Node? b) => b is not null && a.Device == b.Device && a.Inode == b.Inode && a.Kind == b.Kind;
    private static int Fd(SafeFileHandle handle) => handle.DangerousGetHandle().ToInt32();
    private static int DirectoryFlag => OperatingSystem.IsMacOS() ? 0x100000 : RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 0x4000 : 0x10000;
    private static int SafeFlags => OperatingSystem.IsMacOS() ? 0x100 | 0x4 | 0x1000000
        : (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 0x8000 : 0x20000) | 0x800 | 0x80000; // NOFOLLOW, NONBLOCK, CLOEXEC
    private static SafeFileHandle OpenChild(SafeFileHandle parent, string name, bool directory = false, bool write = false)
    {
        var flags = SafeFlags | (directory ? DirectoryFlag : 0);
        if (write) flags |= 1 | (OperatingSystem.IsMacOS() ? 0x200 | 0x800 : 0x40 | 0x80); // WRONLY, CREAT, EXCL
        var fd = OpenAt(Fd(parent), name, flags, 0x1b6);
        if (fd < 0) throw AgentCoreErrors.Validation("Sandbox entry could not be safely opened; links, replacements and inaccessible entries are rejected.");
        return new((IntPtr)fd, ownsHandle: true);
    }
    private static void MakeDirectory(SafeFileHandle parent, string name, int mode, bool exclusive = false)
    {
        if (MkdirAt(Fd(parent), name, mode) != 0 && (exclusive || Marshal.GetLastPInvokeError() != 17))
            throw AgentCoreErrors.Conflict("Sandbox mount directory already exists or cannot be safely created.");
        using var child = OpenChild(parent, name, directory: true);
        if ((Stat(Fd(child)).Mode & 0xfff) != mode && Chmod(Fd(child), mode) != 0) throw new IOException("Sandbox staging permissions could not be set.");
    }
    private static SafeFileHandle Descend(SafeFileHandle start, IEnumerable<string> parts, bool create = false)
    {
        var current = OpenChild(start, ".", directory: true);
        try
        {
            foreach (var part in parts)
            {
                if (create) MakeDirectory(current, part, 0x1ff);
                var next = OpenChild(current, part, directory: true); current.Dispose(); current = next;
            }
            return current;
        }
        catch { current.Dispose(); throw; }
    }
    private SafeFileHandle OpenRoot(bool create = false)
    {
        var path = options.MountRoot();
        // macOS system aliases are fixed OS paths, never workload-controlled links.
        if (OperatingSystem.IsMacOS())
            foreach (var alias in new[] { "/var/", "/tmp/" })
                if (path.StartsWith(alias, StringComparison.Ordinal)) path = "/private" + path;
        var fd = Open("/", SafeFlags | DirectoryFlag);
        if (fd < 0) throw new IOException("Sandbox staging root cannot be opened.");
        using var systemRoot = new SafeFileHandle((IntPtr)fd, true);
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // Existing ancestors must be directories. Only the dedicated root is created.
        using var parent = Descend(systemRoot, parts[..^1]);
        if (create) MakeDirectory(parent, parts[^1], 0x3ff);
        return OpenChild(parent, parts[^1], directory: true);
    }
    private SafeFileHandle OpenWorking(Guid generation)
    {
        using var root = OpenRoot();
        return Descend(root, [generation.ToString("N"), "working"]);
    }
    private static IReadOnlyList<string> Names(SafeFileHandle directory, bool partial = false)
    {
        // fdopendir owns a separately opened description (dup would share the offset).
        using var copy = OpenChild(directory, ".", directory: true);
        var stream = FdOpenDir(Fd(copy));
        if (stream == IntPtr.Zero) throw new IOException("Sandbox directory cannot be enumerated.");
        copy.SetHandleAsInvalid();
        try
        {
            var names = new List<string>();
            while (true)
            {
                Marshal.SetLastPInvokeError(0);
                var item = OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64 ? MacReadDir(stream) : ReadDir(stream);
                if (item == IntPtr.Zero)
                {
                    if (Marshal.GetLastPInvokeError() != 0) throw new IOException("Sandbox directory enumeration failed.");
                    return names;
                }
                var offset = OperatingSystem.IsMacOS() ? 21 : 19;
                var length = 0;
                while (length <= 255 && Marshal.ReadByte(item, offset + length) != 0) length++;
                if (length > 255) throw AgentCoreErrors.Validation("Sandbox filename exceeds limits.");
                var bytes = new byte[length]; Marshal.Copy(item + offset, bytes, 0, length);
                string name;
                try { name = new UTF8Encoding(false, true).GetString(bytes); }
                catch (DecoderFallbackException) { throw AgentCoreErrors.Validation("Sandbox filename is not valid UTF-8."); }
                if (name is "." or "..") continue;
                if (name.Contains('/') || name.Length == 0) throw AgentCoreErrors.Validation("Sandbox directory entry is invalid.");
                if (names.Count >= 8191) { if (partial) return names; throw AgentCoreErrors.WorkspaceQuotaExceeded(); }
                names.Add(name);
            }
        }
        finally { CloseDir(stream); }
    }
    private static Node? TryStat(SafeFileHandle parent, string name)
    {
        var bytes = new byte[256]; var flags = OperatingSystem.IsMacOS() ? 0x20 : 0x100; // AT_SYMLINK_NOFOLLOW
        var result = OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? MacFStatAt(Fd(parent), name, bytes, flags) : FStatAt(Fd(parent), name, bytes, flags);
        if (result == 0) return Decode(bytes);
        if (Marshal.GetLastPInvokeError() == 2) return null;
        throw new IOException("Sandbox staging metadata is unavailable.");
    }
    private static Node Stat(int fd) { var bytes = new byte[256]; if ((OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64 ? MacFStat(fd, bytes) : FStat(fd, bytes)) != 0) throw new IOException("Sandbox file metadata is unavailable."); return Decode(bytes); }
    private static Node Decode(byte[] bytes)
    {
        var mac = OperatingSystem.IsMacOS(); var arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        if (!mac && !OperatingSystem.IsLinux() || !arm && RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("Sandbox staging requires Linux/macOS on x64 or arm64.");
        var mode = mac ? BitConverter.ToUInt16(bytes, 4) : BitConverter.ToUInt32(bytes, arm ? 16 : 24);
        return new(mode & 0xf000, BitConverter.ToUInt64(bytes, 8), mac ? BitConverter.ToUInt16(bytes, 6) : arm ? BitConverter.ToUInt32(bytes, 20) : BitConverter.ToUInt64(bytes, 16),
            BitConverter.ToInt64(bytes, mac ? 96 : 48), BitConverter.ToInt64(bytes, mac ? 48 : 88), BitConverter.ToInt64(bytes, mac ? 56 : 96),
            mac ? BitConverter.ToUInt32(bytes, 0) : BitConverter.ToUInt64(bytes, 0), BitConverter.ToInt64(bytes, mac ? 64 : 104), BitConverter.ToInt64(bytes, mac ? 72 : 112), mode);
    }
    [DllImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)] private static extern int MacFStat(int fd, byte[] buffer);
    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)] private static extern int FStat(int fd, byte[] buffer);
    [DllImport("libc", EntryPoint = "open", SetLastError = true)] private static extern int Open(string path, int flags);
    [DllImport("libc", EntryPoint = "openat", SetLastError = true)] private static extern int OpenAt(int fd, string path, int flags, int mode);
    [DllImport("libc", EntryPoint = "mkdirat", SetLastError = true)] private static extern int MkdirAt(int fd, string path, int mode);
    [DllImport("libc", EntryPoint = "fchmod", SetLastError = true)] private static extern int Chmod(int fd, int mode);
    [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)] private static extern int UnlinkAt(int fd, string path, int flags);
    [DllImport("libc", EntryPoint = "fstatat", SetLastError = true)] private static extern int FStatAt(int fd, string path, byte[] buffer, int flags);
    [DllImport("libc", EntryPoint = "fstatat$INODE64", SetLastError = true)] private static extern int MacFStatAt(int fd, string path, byte[] buffer, int flags);
    [DllImport("libc", EntryPoint = "fdopendir", SetLastError = true)] private static extern IntPtr FdOpenDir(int fd);
    [DllImport("libc", EntryPoint = "readdir", SetLastError = true)] private static extern IntPtr ReadDir(IntPtr dir);
    [DllImport("libc", EntryPoint = "readdir$INODE64", SetLastError = true)] private static extern IntPtr MacReadDir(IntPtr dir);
    [DllImport("libc", EntryPoint = "closedir", SetLastError = true)] private static extern int CloseDir(IntPtr dir);
}
