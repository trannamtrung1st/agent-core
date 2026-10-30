namespace AgentCore.Infrastructure.Persistence;

/// <summary>Single-process ownership of one physical local SQLite installation. Never delete the lock file.</summary>
public static class SqliteInstallationLease
{
    public static IDisposable Acquire(string database)
    {
        try
        {
            var full = Path.GetFullPath(database);
            var path = Path.GetPathRoot(full)!;
            // Native development/maintenance may use OS aliases such as macOS /var.
            // Every alias must converge on the same physical ownership inode.
            foreach (var segment in full[path.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                var component = Path.Combine(path, segment);
                FileSystemInfo entry = Directory.Exists(component) ? new DirectoryInfo(component) : new FileInfo(component);
                path = entry.LinkTarget is not null ? entry.ResolveLinkTarget(returnFinalTarget: true)!.FullName : component;
            }
            var parent = Path.GetDirectoryName(path)!;
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(parent);
            else Directory.CreateDirectory(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var lockPath = path + ".owner.lock";
            if (new FileInfo(lockPath).LinkTarget is not null) throw new IOException();
            var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            var stream = new FileStream(lockPath, options);
            // FileShare.None holds the OS lock for this stream's complete lifetime, including startup/drain.
            return stream;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new InvalidOperationException("SQLite installation ownership unavailable. Stop the other owner and use physical, privately owned local paths. No state was opened.");
        }
    }
}
