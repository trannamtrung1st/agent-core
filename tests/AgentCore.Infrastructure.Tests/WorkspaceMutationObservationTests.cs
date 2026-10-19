using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Infrastructure.Workspaces;

namespace AgentCore.Infrastructure.Tests;

public sealed class WorkspaceMutationObservationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "workspace-mutation-observe-" + Guid.NewGuid().ToString("N"));
    private CanonicalWorkspaceFilesystem Files => new(root, "/home");
    private Task<WorkspaceMutationObservation> Observe(string path, bool follow = false, bool recursive = false, long maxFile = 1024, long maxBytes = 4096) =>
        Files.ObserveMutationAsync([new(path, follow, recursive)], maxFile, maxBytes).AsTask();

    [Fact]
    public async Task Missing_owner_and_parent_observation_never_creates_and_remains_valid_after_ordinary_parent_staging()
    {
        var missing = await Observe("/home/new/folder/file.txt", true); Assert.False(Directory.Exists(root));
        Files.Mkdir("/home/new/folder");
        Assert.Equal(missing, await Observe("/home/new/folder/file.txt", true));
        await Files.WriteAsync("/home/new/folder/file.txt", "present"u8.ToArray(), 1024, 4096);
        Assert.NotEqual(missing.Hash, (await Observe("/home/new/folder/file.txt", true)).Hash);
    }

    [Fact]
    public async Task Binary_body_is_private_and_same_size_timestamp_restored_changes_invalidate_the_fingerprint()
    {
        var bytes = new byte[] { 0, 255, 1, 2, 3 }; await Files.WriteAsync("/home/source.bin", bytes, 1024, 4096);
        var before = await Observe("/home/source.bin", true); Assert.Equal(bytes.Length, before.Bytes); Assert.Equal(64, before.Hash.Length);
        var stamp = File.GetLastWriteTimeUtc(Path.Combine(root, "source.bin"));
        await File.WriteAllBytesAsync(Path.Combine(root, "source.bin"), new byte[] { 0, 255, 1, 2, 4 }); File.SetLastWriteTimeUtc(Path.Combine(root, "source.bin"), stamp);
        Assert.NotEqual(before.Hash, (await Observe("/home/source.bin", true)).Hash);
        Assert.Equal(new[] { "Hash", "Nodes", "Bytes" }, JsonDocument.Parse(JsonSerializer.Serialize(before)).RootElement.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task Structural_links_bind_link_identity_without_reading_external_targets_and_resolved_files_bind_the_actual_route()
    {
        Files.Ensure(); var outside = root + "-outside"; Directory.CreateDirectory(outside);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(outside, "secret"), "private outside bytes");
            File.CreateSymbolicLink(Path.Combine(root, "external"), Path.Combine(outside, "secret"));
            var link = await Observe("/home/external", recursive: true); Assert.Equal(0, link.Bytes);
            await File.WriteAllTextAsync(Path.Combine(outside, "secret"), "changed outside bytes");
            Assert.Equal(link, await Observe("/home/external", recursive: true));
            await Assert.ThrowsAsync<AgentCoreException>(() => Observe("/home/external", true));
            await Files.WriteAsync("/home/a", "same"u8.ToArray(), 1024, 4096); await Files.WriteAsync("/home/b", "same"u8.ToArray(), 1024, 4096);
            File.CreateSymbolicLink(Path.Combine(root, "alias"), "a"); var first = await Observe("/home/alias", true);
            File.Delete(Path.Combine(root, "alias")); File.CreateSymbolicLink(Path.Combine(root, "alias"), "b");
            Assert.NotEqual(first.Hash, (await Observe("/home/alias", true)).Hash);
        }
        finally { Directory.Delete(outside, true); }
    }

    [Fact]
    public async Task Recursive_observation_includes_empty_directories_and_descendant_bytes_without_following_tree_links()
    {
        Files.Mkdir("/home/tree/empty"); await Files.WriteAsync("/home/tree/data", "initial"u8.ToArray(), 1024, 4096);
        File.CreateSymbolicLink(Path.Combine(root, "tree", "loop"), ".");
        var shallow = await Observe("/home/tree"); var before = await Observe("/home/tree", recursive: true);
        Assert.Equal(4, before.Nodes); Assert.Equal(7, before.Bytes);
        await File.WriteAllTextAsync(Path.Combine(root, "tree", "data"), "changed");
        Assert.Equal(shallow, await Observe("/home/tree")); Assert.NotEqual(before.Hash, (await Observe("/home/tree", recursive: true)).Hash);
        var changed = await Observe("/home/tree", recursive: true); Directory.Delete(Path.Combine(root, "tree", "empty"));
        Assert.NotEqual(changed.Hash, (await Observe("/home/tree", recursive: true)).Hash);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task A_file_changed_or_replaced_with_a_link_between_stat_and_open_is_refused(bool link)
    {
        var files = Files; await files.WriteAsync("/home/source", "original"u8.ToArray(), 1024, 4096);
        await files.WriteAsync("/home/other", "other"u8.ToArray(), 1024, 4096);
        files.BeforeOpen = path => { Assert.Equal("/home/source", path); if (link) { File.Delete(Path.Combine(root, "source")); File.CreateSymbolicLink(Path.Combine(root, "source"), "other"); }
            else File.WriteAllText(Path.Combine(root, "source"), "modified"); };
        var error = await Record.ExceptionAsync(() => files.ObserveMutationAsync([new("/home/source", true, false)], 1024, 4096).AsTask());
        Assert.True(error is SecurityConflictException or AgentCoreException);
    }

    [Fact]
    public async Task Observation_refuses_partial_tree_probe_depth_and_byte_coverage_instead_of_issuing_a_truncated_hash()
    {
        await Files.WriteAsync("/home/a", "four"u8.ToArray(), 1024, 4096); await Files.WriteAsync("/home/b", "four"u8.ToArray(), 1024, 4096);
        await Assert.ThrowsAsync<AgentCoreException>(() => Files.ObserveMutationAsync([new("/home/a", true, false), new("/home/b", true, false)], 1024, 7).AsTask());
        await Assert.ThrowsAsync<AgentCoreException>(() => Observe("/home/a", true, maxFile: 3));
        await Assert.ThrowsAsync<AgentCoreException>(() => Files.ObserveMutationAsync(Enumerable.Repeat(new WorkspaceMutationObservationProbe("/home/a", true, false), 33).ToArray(), 1024, 4096).AsTask());
        Files.Mkdir("/home/tree/" + string.Join('/', Enumerable.Repeat("level", 65)));
        await Assert.ThrowsAsync<AgentCoreException>(() => Observe("/home/tree", recursive: true));
        Assert.Equal("four", await File.ReadAllTextAsync(Path.Combine(root, "a")));
    }

    [Fact]
    public async Task Observation_cancellation_returns_no_witness_and_keeps_current_bytes()
    {
        var files = Files; await files.WriteAsync("/home/a", "original"u8.ToArray(), 1024, 4096); using var cancellation = new CancellationTokenSource();
        files.BeforeOpen = _ => cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => files.ObserveMutationAsync([new("/home/a", true, false)], 1024, 4096, cancellation.Token).AsTask());
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(root, "a")));
    }

    [Fact]
    public async Task Probe_input_is_frozen_before_asynchronous_read_and_wide_directory_coverage_fails_closed()
    {
        var files = Files; await files.WriteAsync("/home/a", "a"u8.ToArray(), 1024, 4096); await files.WriteAsync("/home/b", "b"u8.ToArray(), 1024, 4096);
        var probes = new[] { new WorkspaceMutationObservationProbe("/home/a", true, false), new("/home/b", true, false) };
        var expected = await files.ObserveMutationAsync(probes, 1024, 4096);
        files.BeforeOpen = _ => probes[1] = new("/outside/smuggled", true, false);
        Assert.Equal(expected, await files.ObserveMutationAsync(probes, 1024, 4096)); files.BeforeOpen = null;
        files.Mkdir("/home/wide");
        for (var i = 0; i < WorkspaceMutationObservationLimits.MaxNodes; i++) File.WriteAllBytes(Path.Combine(root, "wide", "node-" + i), []);
        await Assert.ThrowsAsync<AgentCoreException>(() => Observe("/home/wide", recursive: true));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
