using System.Runtime.InteropServices;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Sandbox;
using AgentCore.Infrastructure.Workspaces;

namespace AgentCore.Infrastructure.Tests;

public sealed class CanonicalSandboxMountResolverTests : IDisposable
{
    private readonly SpaceTestContext context = new();

    private readonly string _root = Path.Combine(Path.GetTempPath(), "canonical-mounts-" + Guid.NewGuid().ToString("N"));
    private OpenSandboxOptions Options => new() { CanonicalWorkspaceRoot = _root, WorkspaceDockerHostRoot = "/daemon/workspaces" };

    [Theory]
    [InlineData(HomeAccess.ReadWrite, 2, false)]
    [InlineData(HomeAccess.ReadOnly, 2, true)]
    [InlineData(HomeAccess.Disabled, 1, false)]
    public void Mounts_expose_only_exact_owner_scopes_with_effective_home_access(HomeAccess access, int count, bool readOnly)
    {
        var owner = Guid.NewGuid(); var session = Guid.NewGuid();
        var resolver = new CanonicalSandboxMountResolver(Options);
        var mounted = resolver.Resolve(owner, session, context.Access(access), create: true);
        Assert.Equal(count, mounted.Volumes.Count);
        var working = mounted.Volumes.Single(v => v.MountPath == "/working");
        Assert.Equal($"/daemon/workspaces/agent-{owner:N}/sessions/session-{session:N}/working", working.Host!.Path);
        Assert.False(working.ReadOnly);
        Assert.Equal(access, mounted.Authority.HomeAccess);
        Assert.True(mounted.Authority.Uid > 0);
        if (access != HomeAccess.Disabled) Assert.Equal(readOnly, mounted.Volumes.Single(v => v.MountPath == "/home").ReadOnly);
        else Assert.Null(mounted.Home);
        Assert.Equal(mounted.Authority, resolver.Resolve(owner, session, context.Access(access)).Authority);
        var sibling = resolver.Resolve(owner, Guid.NewGuid(), context.Access(access), create: true);
        Assert.NotEqual(mounted.Authority.WorkingIdentity, sibling.Authority.WorkingIdentity);
        Assert.Equal(mounted.Authority.HomeIdentity, sibling.Authority.HomeIdentity);
        var other = resolver.Resolve(Guid.NewGuid(), Guid.NewGuid(), context.Access(access), create: true);
        Assert.NotEqual(mounted.Authority.WorkingIdentity, other.Authority.WorkingIdentity);
    }

    [Fact]
    public async Task Host_created_cross_root_hardlink_alias_prevents_even_a_readonly_mount()
    {
        var owner = Guid.NewGuid(); var session = Guid.NewGuid(); var resolver = new CanonicalSandboxMountResolver(Options);
        var mounted = resolver.Resolve(owner, session, context.Access(HomeAccess.ReadWrite), create: true);
        var home = Path.Combine(mounted.Home!.PhysicalRoot, "private"); await File.WriteAllTextAsync(home, "home");
        Assert.Equal(0, Link(home, Path.Combine(mounted.Working.PhysicalRoot, "alias")));
        await Assert.ThrowsAsync<AgentCoreException>(() => Task.Run(() => resolver.Resolve(owner, session, context.Access(HomeAccess.ReadOnly))));
        Assert.Equal("home", await File.ReadAllTextAsync(home));
    }

    [Fact]
    public void Unproven_strict_quotas_bad_host_mapping_and_substituted_roots_fail_closed()
    {
        Assert.Throws<AgentCoreException>(() => new CanonicalSandboxMountResolver(withHardQuota()).ValidateConfiguration());
        var bad = Options; bad.WorkspaceDockerHostRoot = "/";
        Assert.Throws<ArgumentException>(() => new CanonicalSandboxMountResolver(bad).ValidateConfiguration());
        var owner = Guid.NewGuid(); var session = Guid.NewGuid(); var resolver = new CanonicalSandboxMountResolver(Options);
        var mounted = resolver.Resolve(owner, session, context.Access(HomeAccess.ReadWrite), create: true);
        Directory.Move(mounted.Working.PhysicalRoot, mounted.Working.PhysicalRoot + "-retained");
        Directory.CreateSymbolicLink(mounted.Working.PhysicalRoot, mounted.Home!.PhysicalRoot);
        Assert.Throws<AgentCoreException>(() => resolver.Resolve(owner, session, context.Access(HomeAccess.ReadWrite)));
        var preparation = Assert.Throws<AgentCoreException>(() => resolver.Resolve(owner, session, context.Access(HomeAccess.ReadWrite), create: true));
        Assert.Equal("MountIncompatible", preparation.Code);
        Assert.Contains("mount_preparation", preparation.Message);
        Assert.NotNull(preparation.DiagnosticId);
        OpenSandboxOptions withHardQuota() { var configured = Options; configured.RequireHardStorageQuota = true; return configured; }
    }

    [Fact]
    public void Populated_unmarked_roots_require_explicit_reset_without_deleting_files()
    {
        Directory.CreateDirectory(_root); File.WriteAllText(Path.Combine(_root, "retained"), "old");
        var error = Assert.Throws<AgentCoreException>(() => AgentWorkspacePhysicalPaths.ValidateRoot(_root));
        Assert.Contains("data_reset_required", error.Message);
        Assert.Equal("old", File.ReadAllText(Path.Combine(_root, "retained")));
        Assert.False(File.Exists(Path.Combine(_root, ".canonical-layout")));
    }
    [DllImport("libc", EntryPoint = "link", SetLastError = true)] private static extern int Link(string source, string destination);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
