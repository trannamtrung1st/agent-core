using System.Runtime.InteropServices;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Workspaces;
using OpenSandbox.Models;

namespace AgentCore.Infrastructure.Sandbox;

/// <summary>Exact owner-directory binds. No workload-supplied host path, parent mount or copied tree.</summary>
internal sealed class CanonicalSandboxMountResolver(OpenSandboxOptions options)
{
    internal sealed record Mounts(CanonicalWorkspaceFilesystem Working, CanonicalWorkspaceFilesystem? Home,
        SandboxMountAuthority Authority, IReadOnlyList<Volume> Volumes, string? VerificationRoot = null);

    internal void ValidateConfiguration()
    {
        if (options.RequireHardStorageQuota) throw Incompatible("This adapter provides sampled mounted storage enforcement. Strict kernel quotas have not been qualified on this host.");
        _ = options.CanonicalRoot(); _ = options.DaemonRoot();
        new CanonicalWorkspaceFilesystem(options.CanonicalRoot(), "/working").EnsureLayout();
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw Incompatible("This host cannot provide the required Unix filesystem semantics.");
        if (GetUid() == 0) throw Incompatible("Run Core with a declared non-root Unix identity before direct workspace execution.");
    }

    internal Mounts Resolve(Guid owner, Guid session, HomeAccess access, bool create = false,
        bool validateHardLinks = true, CancellationToken ct = default)
    {
        ValidateConfiguration();
        return Build(AgentWorkspacePhysicalPaths.WorkingDirectory(options.CanonicalRoot(), owner, session),
            AgentWorkspacePhysicalPaths.HomeDirectory(options.CanonicalRoot(), owner), access, create, validateHardLinks, ct);
    }

    internal Mounts Verification(Guid id, HomeAccess access = HomeAccess.ReadWrite, CancellationToken ct = default)
    {
        ValidateConfiguration();
        var root = Path.Combine(options.CanonicalRoot(), ".verification", id.ToString("N"));
        return Build(Path.Combine(root, "working"), Path.Combine(root, "home"), access, ct: ct) with { VerificationRoot = root };
    }

    private Mounts Build(string workingPath, string homePath, HomeAccess access, bool create = true,
        bool validateHardLinks = true, CancellationToken ct = default)
    {
        var working = new CanonicalWorkspaceFilesystem(workingPath, "/working"); if (create) Prepare(working);
        CanonicalWorkspaceFilesystem? home = null;
        if (access != HomeAccess.Disabled) { home = new(homePath, "/home"); if (create) Prepare(home); }
        static string Identity(CanonicalWorkspaceFilesystem files, string path)
        { var node = files.Stat(path); return $"{node.Device:x}:{node.Inode:x}"; }
        if (validateHardLinks) { working.ValidateHardLinkOwnership(ct); home?.ValidateHardLinkOwnership(ct); }
        var identity = new SandboxMountAuthority(Identity(working, "/working"), home is null ? null : Identity(home, "/home"),
            access, checked((int)GetUid()), checked((int)GetGid()), CompatibilityContract: SandboxProfileVerification.CurrentContractVersion);
        List<Volume> volumes = [Volume(workingPath, "working", "/working", false)];
        if (home is not null) volumes.Add(Volume(homePath, "home", "/home", access == HomeAccess.ReadOnly));
        return new(working, home, identity, volumes);
    }

    private static void Prepare(CanonicalWorkspaceFilesystem files)
    {
        try { files.PrepareManagedRoot(GetUid(), GetGid()); }
        catch (Exception error) when (error is AgentCoreException or IOException or UnauthorizedAccessException)
        {
            throw new AgentCoreException("MountIncompatible",
                "Sandbox filesystem is incompatible with this Docker environment. Automatic preparation could not establish safe access (stage: mount_preparation). Canonical files were retained. Repair the trusted deployment and reverify; an identical reset will not help.", 409)
                { DiagnosticId = Guid.CreateVersion7() };
        }
    }

    private Volume Volume(string physical, string name, string logical, bool readOnly)
    {
        var relative = Path.GetRelativePath(options.CanonicalRoot(), physical);
        if (Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar).Contains(".."))
            throw Incompatible("Canonical root and daemon mapping differ.");
        return new() { Name = name, Host = new Host { Path = Path.Combine(options.DaemonRoot(), relative) }, MountPath = logical, ReadOnly = readOnly };
    }

    internal static void CheckUsage(Mounts mounts, long workingLimit, CancellationToken ct)
    {
        // Unknown observations fail closed. Scanning is advisory/sampled; bytes
        // already written are retained when the process is terminated.
        if (mounts.Working.Measure(ct).Bytes > workingLimit
            || mounts.Home is not null && mounts.Home.Measure(ct).Bytes > AgentWorkspaceLimits.MaxInstanceBytes)
            throw AgentCoreErrors.WorkspaceQuotaExceeded();
    }
    internal static AgentCoreException Incompatible(string message) => new("MountIncompatible", message, 409);
    [DllImport("libc", EntryPoint = "geteuid")] private static extern uint GetUid();
    [DllImport("libc", EntryPoint = "getegid")] private static extern uint GetGid();
}
