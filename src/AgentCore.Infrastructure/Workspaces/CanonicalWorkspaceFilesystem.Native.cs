using System.Runtime.InteropServices;
using System.Text;
using AgentCore.Application.Sessions;
using Microsoft.Win32.SafeHandles;

namespace AgentCore.Infrastructure.Workspaces;

internal sealed partial class CanonicalWorkspaceFilesystem
{

    private const uint DirectoryKind = 0x4000, FileKind = 0x8000, LinkKind = 0xa000;
    private static int RemoveDirectoryFlag => OperatingSystem.IsMacOS() ? 0x80 : 0x200;
    private static void ThrowIo(string message)
    {
        var error = Marshal.GetLastPInvokeError();
        throw error switch
        {
            2 => AgentCoreErrors.NotFound("Workspace path was not found."),
            13 or 1 or 40 or 20 or 62 => AgentCoreErrors.Forbidden(message),
            _ => new IOException(message + " (errno " + error + ")")
        };
    }
    private static string ReadLink(SafeFileHandle parent, string name)
    {
        var buffer = new byte[4096];
        var length = ReadLinkAt(Fd(parent), name, buffer, (nuint)buffer.Length);
        if (length < 0 || length >= buffer.Length) throw AgentCoreErrors.Validation("unsafe_link_target: link cannot be inspected.");
        return new UTF8Encoding(false, true).GetString(buffer, 0, checked((int)length));
    }
    [DllImport("libc", EntryPoint = "renameat", SetLastError = true)] private static extern int RenameAt(int oldFd, string oldName, int newFd, string newName);
    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)] private static extern int LinuxRenameAt(int oldFd, string oldName, int newFd, string newName, uint flags);
    [DllImport("libc", EntryPoint = "renameatx_np", SetLastError = true)] private static extern int MacRenameAt(int oldFd, string oldName, int newFd, string newName, uint flags);
    private static int RenameNoReplace(SafeFileHandle source, string from, SafeFileHandle destination, string to) =>
        OperatingSystem.IsMacOS() ? MacRenameAt(Fd(source), from, Fd(destination), to, 4) : LinuxRenameAt(Fd(source), from, Fd(destination), to, 1);
    [DllImport("libc", EntryPoint = "readlinkat", SetLastError = true)] private static extern nint ReadLinkAt(int fd, string name, byte[] buffer, nuint size);
    [DllImport("libc", EntryPoint = "symlinkat", SetLastError = true)] private static extern int SymlinkAt(string target, int fd, string name);
    private sealed record Node(uint Kind, ulong Inode, ulong Links, long Size, long ModifiedSeconds, long ModifiedNanos, ulong Device, long ChangedSeconds, long ChangedNanos, uint Mode, uint Uid, uint Gid);
    private static bool SameIdentity(Node a, Node? b) => b is not null && a.Device == b.Device && a.Inode == b.Inode && a.Kind == b.Kind;
    private static int Fd(SafeFileHandle handle) => handle.DangerousGetHandle().ToInt32();
    private static int DirectoryFlag => OperatingSystem.IsMacOS() ? 0x100000 : RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 0x4000 : 0x10000;
    private static int SafeFlags => OperatingSystem.IsMacOS() ? 0x100 | 0x4 | 0x1000000
        : (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 0x8000 : 0x20000) | 0x800 | 0x80000; // NOFOLLOW, NONBLOCK, CLOEXEC
    private static SafeFileHandle OpenChild(SafeFileHandle parent, string name, bool directory = false, bool write = false)
    {
        var flags = SafeFlags | (directory ? DirectoryFlag : 0);
        if (write) flags |= 1 | (OperatingSystem.IsMacOS() ? 0x200 | 0x800 : 0x40 | 0x80); // WRONLY, CREAT, EXCL
        var fd = OpenAt(Fd(parent), name, flags, 0x180);
        if (fd < 0) ThrowIo("Workspace entry could not be safely opened; links and inaccessible entries require inspection.");
        var handle = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
        // openat's variadic mode ABI differs on macOS arm64. Establish the exact
        // private mode before writing any bytes, independent of that ABI/umask.
        if (write && Chmod(fd, 0x180) != 0)
        {
            handle.Dispose();
            ThrowIo("Workspace file permissions could not be established.");
        }
        return handle;
    }
    private static void MakeDirectory(SafeFileHandle parent, string name, bool exclusive = false)
    {
        var created = MkdirAt(Fd(parent), name, 0x1c0) == 0;
        if (!created && (exclusive || Marshal.GetLastPInvokeError() != 17))
            ThrowIo("Workspace directory could not be created.");
        using var directory = OpenChild(parent, name, directory: true);
        // Pin the exact private mode on newly created directory descriptors;
        // existing arbitrary host ancestors are never chmodded.
        if (created && Chmod(Fd(directory), 0x1c0) != 0) ThrowIo("Workspace directory permissions could not be established.");
    }
    private static SafeFileHandle Descend(SafeFileHandle start, IEnumerable<string> parts, bool create = false)
    {
        var current = OpenChild(start, ".", directory: true);
        try
        {
            foreach (var part in parts)
            {
                if (create) MakeDirectory(current, part);
                var next = OpenChild(current, part, directory: true); current.Dispose(); current = next;
            }
            return current;
        }
        catch { current.Dispose(); throw; }
    }
    private static IReadOnlyList<string> Names(SafeFileHandle directory, bool partial = false, int maximum = ScanBudget)
    {
        // fdopendir owns a separately opened description (dup would share the offset).
        using var copy = OpenChild(directory, ".", directory: true);
        var stream = FdOpenDir(Fd(copy));
        if (stream == IntPtr.Zero) throw new IOException("Workspace directory cannot be enumerated.");
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
                    if (Marshal.GetLastPInvokeError() != 0) throw new IOException("Workspace directory enumeration failed.");
                    return names;
                }
                var offset = OperatingSystem.IsMacOS() ? 21 : 19;
                var length = 0;
                while (length <= 255 && Marshal.ReadByte(item, offset + length) != 0) length++;
                if (length > 255) throw AgentCoreErrors.Validation("Workspace filename exceeds limits.");
                var bytes = new byte[length]; Marshal.Copy(item + offset, bytes, 0, length);
                string name;
                try { name = new UTF8Encoding(false, true).GetString(bytes); }
                catch (DecoderFallbackException) { throw AgentCoreErrors.Validation("Workspace filename is not valid UTF-8."); }
                if (name is "." or "..") continue;
                if (name.Contains('/') || name.Length == 0) throw AgentCoreErrors.Validation("Workspace directory entry is invalid.");
                if (names.Count >= maximum) { if (partial) return names; throw AgentCoreErrors.WorkspaceQuotaExceeded(); }
                names.Add(name);
            }
        }
        finally { CloseDir(stream); }
    }
    private static Node? LStat(SafeFileHandle parent, string name)
    {
        var bytes = new byte[256]; var flags = OperatingSystem.IsMacOS() ? 0x20 : 0x100; // AT_SYMLINK_NOFOLLOW
        var result = OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? MacFStatAt(Fd(parent), name, bytes, flags) : FStatAt(Fd(parent), name, bytes, flags);
        if (result == 0) return Decode(bytes);
        if (Marshal.GetLastPInvokeError() == 2) return null;
        throw new IOException("Workspace filesystem metadata is unavailable.");
    }
    private static Node Metadata(int fd) { var bytes = new byte[256]; if ((OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64 ? MacFStat(fd, bytes) : FStat(fd, bytes)) != 0) throw new IOException("Workspace file metadata is unavailable."); return Decode(bytes); }
    private static Node Decode(byte[] bytes)
    {
        var mac = OperatingSystem.IsMacOS(); var arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        if (!mac && !OperatingSystem.IsLinux() || !arm && RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("Workspace filesystem requires Linux/macOS on x64 or arm64.");
        var mode = mac ? BitConverter.ToUInt16(bytes, 4) : BitConverter.ToUInt32(bytes, arm ? 16 : 24);
        return new(mode & 0xf000, BitConverter.ToUInt64(bytes, 8), mac ? BitConverter.ToUInt16(bytes, 6) : arm ? BitConverter.ToUInt32(bytes, 20) : BitConverter.ToUInt64(bytes, 16),
            BitConverter.ToInt64(bytes, mac ? 96 : 48), BitConverter.ToInt64(bytes, mac ? 48 : 88), BitConverter.ToInt64(bytes, mac ? 56 : 96),
            mac ? BitConverter.ToUInt32(bytes, 0) : BitConverter.ToUInt64(bytes, 0), BitConverter.ToInt64(bytes, mac ? 64 : 104), BitConverter.ToInt64(bytes, mac ? 72 : 112), mode,
            BitConverter.ToUInt32(bytes, mac ? 16 : arm ? 24 : 28), BitConverter.ToUInt32(bytes, mac ? 20 : arm ? 28 : 32));
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
