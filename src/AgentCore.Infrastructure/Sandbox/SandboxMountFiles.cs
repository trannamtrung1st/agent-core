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
    internal Volume Prepare(Guid generation)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) throw new ArgumentException("Sandbox mounts require a Linux or macOS Core host.");
        string root;
        try { root = options.MountRoot(); }
        catch (ArgumentException error) { throw AgentCoreErrors.Validation(error.Message); }
        Directory.CreateDirectory(root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Sandbox mount root cannot be a symbolic link.");
        var parent = Path.GetDirectoryName(Working(generation))!;
        if (Directory.Exists(parent)) throw AgentCoreErrors.Conflict("Sandbox mount generation already exists. Reconcile before retrying.");
        Directory.CreateDirectory(parent); SetMode(parent, 0x1c0);
        Directory.CreateDirectory(Working(generation));
        SetMode(Working(generation), 0x1ff);
        return new Volume { Name = "working", Host = new Host { Path = Path.Combine(Path.GetFullPath(options.WorkspaceMountHostRoot ?? root), generation.ToString("N"), "working") }, MountPath = "/working", ReadOnly = false };
    }

    internal void Delete(Guid generation)
    {
        var parent = Path.GetDirectoryName(Working(generation))!;
        if (!Directory.Exists(parent)) return;
        Remove(parent);
    }
    private static void Remove(string path, int depth = 0)
    {
        if (depth > 64) throw new IOException("Sandbox staging cleanup depth exceeded; retained for inspection.");
        var info = Stat(path);
        if (info.Kind == 0x4000)
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(path)) Remove(child, depth + 1);
            Directory.Delete(path);
        }
        else if (Unlink(path) != 0) throw new IOException("Sandbox staging cleanup failed.");
    }

    internal async Task HydrateAsync(Guid generation, WorkspaceTransfer transfer, CancellationToken ct)
    {
        var root = Working(generation);
        if (Stat(root).Kind != 0x4000) throw AgentCoreErrors.Validation("Sandbox mount directory is invalid.");
        foreach (var child in Directory.EnumerateFileSystemEntries(root)) { ct.ThrowIfCancellationRequested(); Remove(child); }
        foreach (var entry in transfer.Entries.Skip(1).OrderBy(e => e.RelativePath.Count(c => c == '/')))
        {
            ValidatePath(entry.RelativePath);
            var target = Path.Combine(root, entry.RelativePath);
            if (entry.Directory) { Directory.CreateDirectory(target); SetMode(target, 0x1ff); }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllBytesAsync(target, entry.Bytes, ct);
                SetMode(target, 0x1b6); // shared staging file, never canonical bytes
            }
        }
    }

    internal async Task<WorkspaceTransfer> CollectAsync(Guid generation, SandboxProfile profile, CancellationToken ct)
    {
        var first = await CaptureAsync(Working(generation), profile.StorageBytes, ct);
        var second = await CaptureAsync(Working(generation), profile.StorageBytes, ct);
        if (Fingerprint(first) != Fingerprint(second)) throw AgentCoreErrors.Conflict("Sandbox tree changed during collection.");
        return first;
    }
    private static string Fingerprint(WorkspaceTransfer transfer) => string.Join('\n', transfer.Entries.Select(e =>
        e.RelativePath + ":" + e.Directory + ":" + Convert.ToHexString(SHA256.HashData(e.Bytes))));

    private static async Task<WorkspaceTransfer> CaptureAsync(string root, long limit, CancellationToken ct)
    {
        if (Stat(root).Kind != 0x4000) throw AgentCoreErrors.Validation("Sandbox mount directory is invalid.");
        var entries = new List<WorkspaceTransferEntry> { new("", true, "inode/directory", []) };
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0; var files = 0;
        async Task Walk(string directory)
        {
            var children = new List<string>();
            foreach (var child in Directory.EnumerateFileSystemEntries(directory))
            {
                if (children.Count + paths.Count >= 8191) throw AgentCoreErrors.WorkspaceQuotaExceeded();
                children.Add(child);
            }
            foreach (var path in children.Order(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(root, path); ValidatePath(relative);
                if (!paths.Add(relative) || paths.Count > 8191) throw AgentCoreErrors.Validation("Sandbox tree contains colliding or excess paths.");
                var info = Stat(path);
                if (info.Kind == 0x4000)
                {
                    entries.Add(new(relative, true, "inode/directory", [])); await Walk(path);
                }
                else
                {
                    if (info.Kind != 0x8000 || info.Links != 1) throw AgentCoreErrors.Validation("Sandbox links and special files cannot be committed.");
                    if (info.Size < 0 || info.Size > 50L * 1024 * 1024 || ++files > 4096 || (total += info.Size) > limit)
                        throw AgentCoreErrors.WorkspaceQuotaExceeded();
                    entries.Add(new(relative, false, "application/octet-stream", await ReadAsync(path, info, ct)));
                }
            }
        }
        await Walk(root); return new(entries);
    }

    internal async Task ConfirmProbeAsync(Guid generation, CancellationToken ct)
    {
        var path = Path.Combine(Working(generation), ".agent-core-mount-probe");
        var info = Stat(path);
        if (info.Kind != 0x8000 || info.Links != 1 || info.Size != 5 || Encoding.UTF8.GetString(await ReadAsync(path, info, ct)) != "ready")
            throw AgentCoreErrors.Validation("Sandbox /working mount is not shared with Core. Check the platform mount configuration.");
        File.Delete(path);
    }

    private static void SetMode(string path, int mode)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        File.SetUnixFileMode(path, (UnixFileMode)mode);
    }

    private static void ValidatePath(string path)
    {
        if (path.Length is < 1 or > 480 || path.Contains('\\') || path.Any(char.IsControl) || path != path.Normalize(NormalizationForm.FormC)
            || path.Split('/').Length > 8 || path.Split('/').Any(p => p is "" or "." or ".." || p.StartsWith(".env", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".sock", StringComparison.OrdinalIgnoreCase)))
            throw AgentCoreErrors.Validation("Sandbox tree contains an invalid or protected path.");
    }
    private static async Task<byte[]> ReadAsync(string path, Node expected, CancellationToken ct)
    {
        var fd = Open(path, OperatingSystem.IsMacOS() ? 0x100 | 0x4 | 0x1000000 : 0x20000 | 0x800 | 0x80000); // O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC
        if (fd < 0) throw new IOException("Sandbox file could not be safely opened.");
        using var handle = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
        var opened = Stat(fd);
        if (opened != expected || opened.Kind != 0x8000 || opened.Links != 1) throw AgentCoreErrors.Conflict("Sandbox file changed before collection.");
        using var stream = new FileStream(handle, FileAccess.Read);
        var bytes = new byte[checked((int)expected.Size)]; await stream.ReadExactlyAsync(bytes, ct);
        if (Stat(fd) != expected || Stat(path) != expected) throw AgentCoreErrors.Conflict("Sandbox file changed during collection.");
        return bytes;
    }
    private sealed record Node(uint Kind, ulong Inode, ulong Links, long Size, long ModifiedSeconds, long ModifiedNanos);
    private static Node Stat(string path) { var bytes = new byte[256]; if ((OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64 ? MacLStat(path, bytes) : LStat(path, bytes)) != 0) throw new IOException("Sandbox staging metadata is unavailable."); return Decode(bytes); }
    private static Node Stat(int fd) { var bytes = new byte[256]; if ((OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64 ? MacFStat(fd, bytes) : FStat(fd, bytes)) != 0) throw new IOException("Sandbox file metadata is unavailable."); return Decode(bytes); }
    private static Node Decode(byte[] bytes)
    {
        var mac = OperatingSystem.IsMacOS(); var arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        if (!mac && !OperatingSystem.IsLinux() || !arm && RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("Sandbox staging requires Linux/macOS on x64 or arm64.");
        var mode = mac ? BitConverter.ToUInt16(bytes, 4) : BitConverter.ToUInt32(bytes, arm ? 16 : 24);
        return new(mode & 0xf000, BitConverter.ToUInt64(bytes, 8), mac ? BitConverter.ToUInt16(bytes, 6) : arm ? BitConverter.ToUInt32(bytes, 20) : BitConverter.ToUInt64(bytes, 16),
            BitConverter.ToInt64(bytes, mac ? 96 : 48), BitConverter.ToInt64(bytes, mac ? 48 : 88), BitConverter.ToInt64(bytes, mac ? 56 : 96));
    }
    [DllImport("libc", EntryPoint = "lstat$INODE64", SetLastError = true)] private static extern int MacLStat(string path, byte[] buffer);
    [DllImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)] private static extern int MacFStat(int fd, byte[] buffer);
    [DllImport("libc", EntryPoint = "lstat", SetLastError = true)] private static extern int LStat(string path, byte[] buffer);
    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)] private static extern int FStat(int fd, byte[] buffer);
    [DllImport("libc", EntryPoint = "open", SetLastError = true)] private static extern int Open(string path, int flags);
    [DllImport("libc", EntryPoint = "unlink", SetLastError = true)] private static extern int Unlink(string path);
}
