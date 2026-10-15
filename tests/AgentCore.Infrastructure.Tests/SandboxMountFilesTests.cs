using System.Runtime.InteropServices;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Sandbox;

namespace AgentCore.Infrastructure.Tests;

public sealed class SandboxMountFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sandbox-mount-test-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _generation = Guid.NewGuid();
    private SandboxMountFiles Files => new(new() { WorkspaceMountRoot = _root });
    private static SandboxProfile Profile => new("fixture", 1, "Fixture", "", true, "sha256:" + new string('a', 64));

    [Fact]
    public async Task Hydration_collection_and_cleanup_use_only_the_private_generation_directory()
    {
        var volume = Files.Prepare(_generation);
        Assert.Equal("/working", volume.MountPath); Assert.False(volume.ReadOnly);
        Assert.Equal(Files.Working(_generation), volume.Host!.Path);
        await Files.HydrateAsync(_generation, new([new("", true, "inode/directory", []), new("nested", true, "inode/directory", []), new("nested/file.txt", false, "text/plain", "safe"u8.ToArray())]), default);
        var captured = await Files.CollectAsync(_generation, Profile, default);
        Assert.Equal("safe"u8.ToArray(), captured.Entries.Single(e => e.RelativePath == "nested/file.txt").Bytes);
        Files.Delete(_generation); Assert.False(Directory.Exists(Path.GetDirectoryName(volume.Host.Path)));
    }

    [Theory]
    [InlineData("symlink")]
    [InlineData("directoryLink")]
    [InlineData("hardLink")]
    [InlineData("fifo")]
    [InlineData("caseCollision")]
    [InlineData("protected")]
    public async Task Unsafe_workload_files_are_rejected_without_following_links_or_blocking_on_special_files(string kind)
    {
        Files.Prepare(_generation); var working = Files.Working(_generation);
        var outside = Path.Combine(_root, "outside"); Directory.CreateDirectory(outside);
        var original = Path.Combine(outside, "retained.txt"); await File.WriteAllTextAsync(original, "retained");
        switch (kind)
        {
            case "symlink": File.CreateSymbolicLink(Path.Combine(working, "escape"), original); break;
            case "directoryLink": Directory.CreateSymbolicLink(Path.Combine(working, "escape"), outside); break;
            case "hardLink": Assert.Equal(0, Link(original, Path.Combine(working, "linked"))); break;
            case "fifo": Assert.Equal(0, MkFifo(Path.Combine(working, "pipe"), 0x180)); break;
            case "caseCollision":
                Directory.CreateDirectory(Path.Combine(working, "Sub"));
                await File.WriteAllTextAsync(Path.Combine(working, "Sub", "a"), "a");
                // Case-insensitive host filesystems cannot represent this fixture distinctly.
                if (Directory.Exists(Path.Combine(working, "sub"))) { await File.WriteAllTextAsync(Path.Combine(working, ".env.fixture"), "blocked"); break; }
                Directory.CreateDirectory(Path.Combine(working, "sub")); break;
            case "protected": await File.WriteAllTextAsync(Path.Combine(working, ".env.fixture"), "blocked"); break;
        }
        await Assert.ThrowsAsync<AgentCoreException>(() => Files.CollectAsync(_generation, Profile, default));
        Files.Delete(_generation);
        Assert.Equal("retained", await File.ReadAllTextAsync(original));
    }

    [Fact]
    public async Task Quota_is_checked_before_allocating_file_bytes_and_probe_requires_actual_write_through()
    {
        Files.Prepare(_generation);
        await File.WriteAllTextAsync(Path.Combine(Files.Working(_generation), "data"), "oversize");
        await Assert.ThrowsAsync<AgentCoreException>(() => Files.CollectAsync(_generation, Profile with { StorageBytes = 1 }, default));
        await Assert.ThrowsAnyAsync<Exception>(() => Files.ConfirmProbeAsync(_generation, default));
        await File.WriteAllTextAsync(Path.Combine(Files.Working(_generation), ".agent-core-mount-probe"), "ready");
        await Files.ConfirmProbeAsync(_generation, default);
        Assert.False(File.Exists(Path.Combine(Files.Working(_generation), ".agent-core-mount-probe")));
    }
    [Fact]
    public void Host_path_mapping_is_explicit_and_generation_cannot_be_reused()
    {
        var mapped = new SandboxMountFiles(new() { WorkspaceMountRoot = _root, WorkspaceMountHostRoot = "/platform/staging" });
        Assert.Equal("/platform/staging/" + _generation.ToString("N") + "/working", mapped.Prepare(_generation).Host!.Path);
        Assert.ThrowsAny<Exception>(() => mapped.Prepare(_generation));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/tmp/..")]
    [InlineData("/tmp/../")]
    [InlineData("relative/staging")]
    public void Filesystem_roots_and_relative_paths_are_rejected_for_both_sides_of_the_mount(string path)
    {
        Assert.Throws<ArgumentException>(() => new OpenSandboxOptions { WorkspaceMountRoot = path }.MountRoot());
        Assert.Throws<ArgumentException>(() => new OpenSandboxOptions { WorkspaceMountRoot = _root, WorkspaceMountHostRoot = path }.MountRoot());
        Assert.False(Directory.Exists(_root));
    }
    public void Dispose() { if (Directory.Exists(_root)) { Files.Delete(_generation); Directory.Delete(_root, true); } }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Directory_replacement_between_metadata_open_and_descendant_read_never_follows_a_link(bool afterOpen)
    {
        var files = Files; files.Prepare(_generation);
        var outside = Path.Combine(_root, "foreign"); Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "secret.txt"), "foreign-sentinel");
        var path = Path.Combine(files.Working(_generation), "nested"); Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(Path.Combine(path, "owned.txt"), "owned");
        var swapped = false;
        files.CollectionFault = relative => {
            if (swapped || relative != (afterOpen ? "nested/owned.txt" : "nested")) return;
            swapped = true; Directory.Move(path, path + "-retained"); Directory.CreateSymbolicLink(path, outside);
        };
        await Assert.ThrowsAsync<AgentCoreException>(() => files.CollectAsync(_generation, Profile, default));
        Assert.True(swapped); files.Delete(_generation);
        Assert.Equal("foreign-sentinel", await File.ReadAllTextAsync(Path.Combine(outside, "secret.txt")));
    }

    [Fact]
    public void Mount_root_links_are_rejected_and_live_staging_usage_is_bounded_without_reading_contents()
    {
        var outside = Path.Combine(_root, "foreign"); Directory.CreateDirectory(outside);
        var link = Path.Combine(_root, "alias"); Directory.CreateSymbolicLink(link, outside);
        var linked = new SandboxMountFiles(new() { WorkspaceMountRoot = link });
        Assert.Throws<AgentCoreException>(() => linked.Prepare(Guid.NewGuid()));
        var files = Files; files.Prepare(_generation);
        File.WriteAllBytes(Path.Combine(files.Working(_generation), "data"), new byte[32]);
        Assert.Throws<AgentCoreException>(() => files.CheckLiveUsage(_generation, 16, default));
        files.CheckLiveUsage(_generation, 32, default);
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal((UnixFileMode)0x3ff, File.GetUnixFileMode(_root));
        Assert.Equal((UnixFileMode)0x1c0, File.GetUnixFileMode(Path.GetDirectoryName(files.Working(_generation))!));
        Assert.Equal((UnixFileMode)0x1ff, File.GetUnixFileMode(files.Working(_generation)));
    }
    [DllImport("libc", EntryPoint = "link", SetLastError = true)] private static extern int Link(string source, string target);
    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)] private static extern int MkFifo(string path, uint mode);
}
