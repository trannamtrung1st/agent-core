using System.Runtime.InteropServices;
using System.Security.Cryptography;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Workspaces;

namespace AgentCore.Infrastructure.Tests;

public sealed class WorkspaceArtifactSourceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "workspace-artifact-source-" + Guid.NewGuid().ToString("N"));
    private CanonicalWorkspaceFilesystem Files => new(Path.Combine(root, "home"), "/home");
    private Task<CanonicalWorkspaceFilesystem.ArtifactSource> Capture(CanonicalWorkspaceFilesystem files, string path = "/home/source", long max = 4096, bool content = true,
        CancellationToken ct = default) => files.CaptureArtifactSourceAsync(path, max, content, ct).AsTask();

    [Fact]
    public async Task Missing_source_does_not_initialize_its_root_and_empty_file_is_complete()
    {
        var files = Files;
        await Assert.ThrowsAsync<AgentCoreException>(() => Capture(files)); Assert.False(Directory.Exists(root));
        await files.WriteAsync("/home/source", Array.Empty<byte>(), 4096, 8192);
        var captured = await Capture(files, max: 0); Assert.Empty(captured.Bytes); Assert.Equal(0, captured.ByteSize);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData([])), captured.Sha256Hex);
        Assert.Equal(captured.ObservationHash, (await Capture(files, max: 0, content: false)).ObservationHash);
    }

    [Fact]
    public async Task Export_checks_the_50_MiB_file_ceiling_without_applying_the_Office_processing_ceiling()
    {
        var files = Files; files.Ensure(); var physical = Path.Combine(files.PhysicalRoot, "source");
        using (var file = new FileStream(physical, FileMode.CreateNew)) file.SetLength(ArtifactLimits.MaxBytesEach + 1);
        var error = await Assert.ThrowsAsync<AgentCoreException>(() => Capture(files, max: ArtifactLimits.MaxBytesEach));
        Assert.Equal("WorkspaceQuotaExceeded", error.Code);
        using (var file = new FileStream(physical, FileMode.Open)) file.SetLength(ArtifactLimits.MaxBytesEach);
        var source = await Capture(files, max: ArtifactLimits.MaxBytesEach);
        Assert.Equal(ArtifactLimits.MaxBytesEach, source.ByteSize); Assert.Equal(ArtifactLimits.MaxBytesEach, source.Bytes.LongLength);
        Assert.Equal(source.Sha256Hex, Convert.ToHexStringLower(SHA256.HashData(source.Bytes)));
        Assert.Equal(source.ObservationHash, (await Capture(files, max: ArtifactLimits.MaxBytesEach, content: false)).ObservationHash);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Configured_byte_bound_and_invalid_bounds_refuse_partial_or_unbounded_sources(bool content)
    {
        var files = Files; await files.WriteAsync("/home/source", "exact bytes"u8.ToArray(), 4096, 8192);
        await Assert.ThrowsAsync<AgentCoreException>(() => Capture(files, max: 10, content: content));
        await Assert.ThrowsAsync<AgentCoreException>(() => Capture(files, max: -1, content: content));
        await Assert.ThrowsAsync<AgentCoreException>(() => Capture(files, max: (long)int.MaxValue + 1, content: content));
        var source = await Capture(files, max: 11, content: content); Assert.Equal(11, source.ByteSize);
        if (content) Assert.Equal("exact bytes"u8.ToArray(), source.Bytes); else Assert.Empty(source.Bytes);
    }

    [Theory] [InlineData("/home")] [InlineData("/home/directory")] [InlineData("/working/source")] [InlineData("/home/../outside")]
    public async Task Root_directory_and_out_of_scope_paths_are_not_file_sources(string path)
    {
        var files = Files; files.Mkdir("/home/directory");
        await Assert.ThrowsAsync<AgentCoreException>(() => Capture(files, path));
    }

    [Fact]
    public async Task Source_witness_binds_internal_link_identity_route_and_target_with_no_external_traversal()
    {
        var files = Files; await files.WriteAsync("/home/a", "same"u8.ToArray(), 4096, 8192); await files.WriteAsync("/home/b", "same"u8.ToArray(), 4096, 8192);
        var alias = Path.Combine(files.PhysicalRoot, "source"); File.CreateSymbolicLink(alias, "a");
        var first = await Capture(files); Assert.Equal("same"u8.ToArray(), first.Bytes);
        File.Delete(alias); File.CreateSymbolicLink(alias, "b"); var second = await Capture(files);
        Assert.Equal(first.Sha256Hex, second.Sha256Hex); Assert.NotEqual(first.ObservationHash, second.ObservationHash);
        // Even an identically spelled replacement link invalidates saved evidence.
        File.Delete(alias); File.CreateSymbolicLink(alias, "b"); Assert.NotEqual(second.ObservationHash, (await Capture(files)).ObservationHash);
        var outside = Path.Combine(root, "outside"); await File.WriteAllTextAsync(outside, "private outside content");
        File.Delete(alias); File.CreateSymbolicLink(alias, outside);
        await Assert.ThrowsAsync<AgentCoreException>(() => Capture(files)); Assert.Equal("private outside content", await File.ReadAllTextAsync(outside));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Swapped_file_or_symbolic_link_at_open_never_returns_a_capture(bool link)
    {
        var files = Files; await files.WriteAsync("/home/source", "original"u8.ToArray(), 4096, 8192);
        await files.WriteAsync("/home/other", "original"u8.ToArray(), 4096, 8192);
        files.BeforeOpen = _ =>
        {
            if (link) { File.Delete(Path.Combine(files.PhysicalRoot, "source")); File.CreateSymbolicLink(Path.Combine(files.PhysicalRoot, "source"), "other"); }
            else File.WriteAllText(Path.Combine(files.PhysicalRoot, "source"), "modified");
        };
        var error = await Record.ExceptionAsync(() => Capture(files)); Assert.True(error is SecurityConflictException or AgentCoreException);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Replacing_parent_or_owner_root_at_open_never_returns_detached_old_bytes(bool ownerRoot)
    {
        var files = Files; files.Mkdir("/home/folder"); await files.WriteAsync("/home/folder/source", "original"u8.ToArray(), 4096, 8192);
        files.BeforeOpen = _ =>
        {
            var old = ownerRoot ? files.PhysicalRoot : Path.Combine(files.PhysicalRoot, "folder");
            Directory.Move(old, old + "-detached"); Directory.CreateDirectory(old);
            if (ownerRoot) Directory.CreateDirectory(Path.Combine(old, "folder"));
            File.WriteAllText(Path.Combine(old, ownerRoot ? "folder/source" : "source"), "original");
        };
        await Assert.ThrowsAsync<SecurityConflictException>(() => Capture(files, "/home/folder/source"));
    }

    [Fact]
    public async Task Internal_hard_links_remain_owned_and_external_aliases_are_refused()
    {
        var files = Files; await files.WriteAsync("/home/source", "private"u8.ToArray(), 4096, 8192);
        Assert.Equal(0, Link(Path.Combine(files.PhysicalRoot, "source"), Path.Combine(files.PhysicalRoot, "internal")));
        var captured = await Capture(files); Assert.Equal("private"u8.ToArray(), captured.Bytes);
        Assert.Equal(0, Link(Path.Combine(files.PhysicalRoot, "source"), Path.Combine(root, "external")));
        await Assert.ThrowsAsync<AgentCoreException>(() => Capture(files));
    }

    [Fact]
    public async Task Unrelated_sibling_changes_preserve_source_evidence_and_cancellation_returns_no_capture()
    {
        var files = Files; await files.WriteAsync("/home/source", "private"u8.ToArray(), 4096, 8192); var before = await Capture(files);
        await files.WriteAsync("/home/sibling", "new"u8.ToArray(), 4096, 8192); files.Mkdir("/home/new-directory");
        Assert.Equal(before.ObservationHash, (await Capture(files)).ObservationHash);
        using var cancellation = new CancellationTokenSource(); files.BeforeOpen = _ => cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Capture(files, ct: cancellation.Token));
        Assert.Equal("private", await File.ReadAllTextAsync(Path.Combine(files.PhysicalRoot, "source")));
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true)] private static extern int Link(string source, string destination);
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
