using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Sandbox;
using AgentCore.Infrastructure.Workspaces;

namespace AgentCore.Infrastructure.Tests;

public sealed class SandboxFilesystemPreparationTests : IDisposable
{
    private readonly SpaceTestContext context = new();

    private readonly string _root = Path.Combine(Path.GetTempPath(), "sandbox-preparation-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Concurrent_sessions_prepare_shared_home_once_and_repair_only_owned_root_permissions()
    {
        if (OperatingSystem.IsWindows()) return;
        var options = new OpenSandboxOptions { CanonicalWorkspaceRoot = _root, WorkspaceDockerHostRoot = _root };
        var resolver = new CanonicalSandboxMountResolver(options);
        var owner = Guid.NewGuid(); var first = Guid.NewGuid();
        var mounted = resolver.Resolve(owner, first, context.Access(HomeAccess.ReadWrite), create: true);
        var file = Path.Combine(mounted.Home!.PhysicalRoot, "retained");
        await File.WriteAllTextAsync(file, "original");
        var modified = File.GetLastWriteTimeUtc(file);
        var nested = Path.Combine(mounted.Home.PhysicalRoot, "project");
        Directory.CreateDirectory(nested); File.SetUnixFileMode(nested, (UnixFileMode)0x1ed);
        File.SetUnixFileMode(mounted.Home.PhysicalRoot, (UnixFileMode)0x1ed);
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => resolver.Resolve(owner, Guid.NewGuid(), context.Access(HomeAccess.ReadWrite), create: true))));
        Assert.All(results, result => Assert.Equal(mounted.Authority.HomeIdentity, result.Authority.HomeIdentity));
        Assert.Equal((UnixFileMode)0x1c0, File.GetUnixFileMode(mounted.Home.PhysicalRoot));
        Assert.Equal((UnixFileMode)0x1ed, File.GetUnixFileMode(nested));
        Assert.Equal("original", await File.ReadAllTextAsync(file));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(file));
        Assert.Equal(mounted.Authority, resolver.Resolve(owner, first, context.Access(HomeAccess.ReadWrite), create: true).Authority);
    }

    [Fact]
    public void New_directories_are_private_and_foreign_identity_is_never_repaired()
    {
        var files = new CanonicalWorkspaceFilesystem(Path.Combine(_root, "owned"), "/working");
        files.Ensure(); var node = files.Stat("/working");
        Assert.Equal(0x1c0u, node.Mode);
        Assert.Throws<AgentCoreException>(() => files.PrepareManagedRoot(node.Uid + 1, node.Gid));
        Assert.Equal(node, files.Stat("/working"));
        var target = Path.Combine(_root, "foreign"); Directory.CreateDirectory(target);
        var link = Path.Combine(_root, "link"); Directory.CreateSymbolicLink(link, target);
        var linked = new CanonicalWorkspaceFilesystem(link, "/working");
        Assert.Throws<AgentCoreException>(() => linked.PrepareManagedRoot(node.Uid, node.Gid));
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
    }

    [Fact]
    public void Earlier_generation_authority_does_not_silently_adopt_the_new_compatibility_contract()
    {
        var historical = System.Text.Json.JsonSerializer.Deserialize<SandboxMountAuthority>("""{"WorkingIdentity":"working","HomeIdentity":null,"HomeAccess":0,"Uid":501,"Gid":20}""")!;
        Assert.Equal(0, historical.CompatibilityContract);
        var resolver = new CanonicalSandboxMountResolver(new() { CanonicalWorkspaceRoot = _root, WorkspaceDockerHostRoot = _root });
        var current = resolver.Resolve(Guid.NewGuid(), Guid.NewGuid(), context.Access(HomeAccess.Disabled), create: true).Authority;
        Assert.Equal(SandboxProfileVerification.CurrentContractVersion, current.CompatibilityContract);
        Assert.NotEqual(current, current with { CompatibilityContract = historical.CompatibilityContract });
    }

    [Fact]
    public void Scoped_Git_trust_is_command_local_exact_policy_limited_and_disabled_on_native_linux()
    {
        var authority = new SandboxMountAuthority("working", "home", HomeAccess.ReadWrite, 501, 20);
        Assert.Single(OpenSandboxService.GitEnvironment(authority));
        var environment = OpenSandboxService.GitEnvironment(authority with { TranslatedOwnership = true }, ["/working/project", "/home/project"]);
        Assert.Equal("5", environment["GIT_CONFIG_COUNT"]);
        Assert.Equal("", environment["GIT_CONFIG_VALUE_0"]);
        Assert.Equal("/working", environment["GIT_CONFIG_VALUE_1"]);
        Assert.Equal("/home", environment["GIT_CONFIG_VALUE_2"]);
        Assert.DoesNotContain("*", environment.Values);
        var disabled = authority with { HomeAccess = HomeAccess.Disabled, TranslatedOwnership = true };
        Assert.DoesNotContain("/home", OpenSandboxService.GitEnvironment(disabled).Values);
        foreach (var path in new[] { "/other", "/home/project", "/working/../sibling", "/working/*" })
            Assert.Throws<AgentCoreException>(() => OpenSandboxService.GitEnvironment(disabled, [path]));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
