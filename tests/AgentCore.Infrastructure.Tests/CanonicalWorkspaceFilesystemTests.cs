using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using AgentCore.Application.Sessions;
using AgentCore.Infrastructure.Workspaces;

namespace AgentCore.Infrastructure.Tests;

public sealed class CanonicalWorkspaceFilesystemTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "canonical-workspace-" + Guid.NewGuid().ToString("N"));
    private CanonicalWorkspaceFilesystem Home => new(Path.Combine(_root, "agent-a", "home"), "/home");
    private CanonicalWorkspaceFilesystem Working(string session = "a") => new(Path.Combine(_root, "agent-a", "sessions", session, "working"), "/working");
    private static async Task Write(CanonicalWorkspaceFilesystem fs, string path, string text) =>
        await fs.WriteAsync(path, Encoding.UTF8.GetBytes(text), 1024 * 1024, 16 * 1024 * 1024);
    private static async Task<string> Read(CanonicalWorkspaceFilesystem fs, string path) =>
        Encoding.UTF8.GetString(await fs.ReadAsync(path, 1024 * 1024));

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Protected_mutation_callback_rejection_or_cancellation_keeps_destination_and_cleans_staging(bool delete, bool cancel)
    {
        var home = Home; await Write(home, "/home/original.txt", "original");
        using var cancellation = new CancellationTokenSource(); var observed = false;
        ValueTask Authorize(CancellationToken token)
        {
            observed = true; Assert.Equal("original", File.ReadAllText(Path.Combine(home.PhysicalRoot, "original.txt")));
            if (cancel) { cancellation.Cancel(); return ValueTask.CompletedTask; }
            throw AgentCoreErrors.Forbidden("Authorization changed.");
        }
        var error = await Record.ExceptionAsync(() => delete
            ? home.DeleteAuthorizedAsync("/home/original.txt", Authorize, cancellation.Token).AsTask()
            : home.WriteAsync("/home/original.txt", "replacement"u8.ToArray(), 1024, 4096, cancellation.Token, Authorize).AsTask());
        Assert.True(observed); Assert.NotNull(error);
        if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(error); else Assert.IsType<AgentCoreException>(error);
        Assert.Equal("original", await Read(home, "/home/original.txt"));
        Assert.Equal(new[] { "original.txt" }, Directory.EnumerateFileSystemEntries(home.PhysicalRoot).Select(p => Path.GetFileName(p)!).ToArray());
    }

    [Fact]
    public async Task Approved_asset_reads_never_follow_links_or_a_file_swapped_at_open()
    {
        var home = Home; home.Ensure(); home.Mkdir("/home/approved");
        await Write(home, "/home/approved/note.txt", "approved"); await Write(home, "/home/other.txt", "other approved-area file");
        File.CreateSymbolicLink(Path.Combine(home.PhysicalRoot, "alias.txt"), "other.txt");
        Directory.CreateSymbolicLink(Path.Combine(home.PhysicalRoot, "alias-dir"), "approved");
        Assert.Equal("other approved-area file", await Read(home, "/home/alias.txt"));
        await Assert.ThrowsAsync<AgentCoreException>(() => home.ReadUnlinkedAsync("/home/alias.txt", 1024).AsTask());
        Assert.Throws<AgentCoreException>(() => home.ListUnlinked("/home/alias-dir"));
        home.BeforeOpen = _ =>
        {
            var path = Path.Combine(home.PhysicalRoot, "approved", "note.txt");
            File.Delete(path); File.CreateSymbolicLink(path, "../other.txt");
        };
        await Assert.ThrowsAsync<AgentCoreException>(() => home.ReadUnlinkedAsync("/home/approved/note.txt", 1024).AsTask());
        home.BeforeOpen = null;
        Assert.Equal("other approved-area file", await Read(home, "/home/other.txt"));
    }

    [Theory]
    [InlineData("file")]
    [InlineData("directory")]
    [InlineData("symlink")]
    public async Task Exclusive_publication_preserves_a_concurrent_external_destination(string kind)
    {
        var working = Working(); working.Ensure();
        var target = Path.Combine(working.PhysicalRoot, "attachment.txt");
        await Write(working, "/working/original", "original");
        working.BeforeOpen = _ =>
        {
            if (kind == "file") File.WriteAllText(target, "external");
            else if (kind == "directory") Directory.CreateDirectory(target);
            else File.CreateSymbolicLink(target, "original");
        };
        Assert.False(await working.TryWriteNewAsync("/working/attachment.txt", "attachment"u8.ToArray(), 1024, 4096));
        working.BeforeOpen = null;
        Assert.Equal("original", await Read(working, "/working/original"));
        if (kind == "file") Assert.Equal("external", await File.ReadAllTextAsync(target));
        else Assert.Equal(kind, working.Stat("/working/attachment.txt").Kind);
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(working.PhysicalRoot), p => p.EndsWith(".write", StringComparison.Ordinal));
        working.BeforeOpen = null;
        Assert.True(await working.TryWriteNewAsync("/working/attachment-2.txt", "attachment"u8.ToArray(), 1024, 4096));
        Assert.Equal("attachment", await Read(working, "/working/attachment-2.txt"));
    }

    [Fact]
    public async Task Present_bytes_are_shared_immediately_and_reopen_keeps_normal_names_and_empty_directories()
    {
        var home = Home; home.Ensure(); var a = Working(); a.Ensure(); var b = Working("b"); b.Ensure();
        var deep = "/home/" + string.Join('/', Enumerable.Repeat("deep", 12));
        home.Mkdir(deep + "/empty");
        await Write(home, deep + "/.env.local", "tool");
        var physical = Path.Combine(home.PhysicalRoot, deep[6..], ".env.local");
        Assert.Equal("tool", await File.ReadAllTextAsync(physical));
        await File.WriteAllTextAsync(physical, "external writer");
        Assert.Equal("external writer", await Read(Home, deep + "/.env.local"));
        Assert.True(Home.Stat(deep + "/empty").Directory);
        Assert.Equal("external writer", await Read(Home, deep + "/.env.local"));
        await Write(a, "/working/identical.txt", "A"); await Write(b, "/working/identical.txt", "B");
        Assert.Equal("A", await Read(Working(), "/working/identical.txt"));
        Assert.Equal("B", await Read(Working("b"), "/working/identical.txt"));
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(home.PhysicalRoot), p => Path.GetFileName(p) == "blobs");
    }

    [Fact]
    public async Task File_edits_observe_current_content_without_a_prior_hash_and_keep_mode()
    {
        var home = Home; home.Ensure();
        await Write(home, "/home/file", "one");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(home.PhysicalRoot, "file"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await File.WriteAllTextAsync(Path.Combine(home.PhysicalRoot, "file"), "another writer");
        await Write(home, "/home/file", "two");
        Assert.Equal("two", await Read(home, "/home/file"));
        if (!OperatingSystem.IsWindows()) Assert.Equal(0x1c0u, home.Stat("/home/file").Mode);
        Assert.Single(home.List("/home").Entries);
    }

    [Fact]
    public async Task Safe_links_are_inspectable_readable_copyable_and_unlinked_without_following_targets()
    {
        var home = Home; home.Ensure();
        await Write(home, "/home/data", "retained");
        File.CreateSymbolicLink(Path.Combine(home.PhysicalRoot, "relative"), "data");
        File.CreateSymbolicLink(Path.Combine(home.PhysicalRoot, "absolute"), "/home/data");
        Assert.Equal("retained", await Read(home, "/home/relative"));
        Assert.Equal("retained", await Read(home, "/home/absolute"));
        Assert.Equal("symlink", home.Stat("/home/relative").Kind);
        Assert.Equal("data", home.Stat("/home/relative").LinkTarget);
        home.CopyTo("/home/relative", home, "/home/copied");
        Assert.Equal("data", home.Stat("/home/copied").LinkTarget);
        home.Delete("/home/relative", recursive: true);
        Assert.Equal("retained", await Read(home, "/home/data"));
        Directory.CreateSymbolicLink(Path.Combine(home.PhysicalRoot, "escape"), _root);
        var error = await Assert.ThrowsAsync<AgentCoreException>(() => Read(home, "/home/escape/foreign"));
        Assert.Contains("unsafe_link_target", error.Message);
        home.Delete("/home/escape", true);
        Assert.True(Directory.Exists(_root));
    }

    [Fact]
    public async Task Directory_or_file_swap_to_a_link_never_reads_a_foreign_inode()
    {
        var home = Home; home.Ensure();
        var foreign = Path.Combine(_root, "foreign"); Directory.CreateDirectory(foreign);
        await File.WriteAllTextAsync(Path.Combine(foreign, "secret"), "foreign-sentinel");
        await Write(home, "/home/nested/secret", "owned");
        home.BeforeOpen = _ =>
        {
            Directory.Move(Path.Combine(home.PhysicalRoot, "nested"), Path.Combine(home.PhysicalRoot, "original"));
            Directory.CreateSymbolicLink(Path.Combine(home.PhysicalRoot, "nested"), foreign);
        };
        // The parent descriptor already names the owned directory even if its
        // pathname is swapped before the file open.
        Assert.Equal("owned", await Read(home, "/home/nested/secret"));
        home.BeforeOpen = _ =>
        {
            File.Delete(Path.Combine(home.PhysicalRoot, "original", "secret"));
            File.CreateSymbolicLink(Path.Combine(home.PhysicalRoot, "original", "secret"), Path.Combine(foreign, "secret"));
        };
        await Assert.ThrowsAsync<AgentCoreException>(() => Read(home, "/home/original/secret"));
        Assert.Equal("foreign-sentinel", await File.ReadAllTextAsync(Path.Combine(foreign, "secret")));
    }

    [Fact]
    public async Task Within_root_hardlinks_work_but_a_host_created_cross_root_alias_fails_closed()
    {
        var home = Home; home.Ensure(); var working = Working(); working.Ensure();
        await Write(home, "/home/data", "original");
        Assert.Equal(0, Link(Path.Combine(home.PhysicalRoot, "data"), Path.Combine(home.PhysicalRoot, "alias")));
        Assert.Equal("original", await Read(home, "/home/alias"));
        await Write(home, "/home/data", "replacement");
        Assert.Equal("original", await Read(home, "/home/alias"));
        Assert.Equal("replacement", await Read(home, "/home/data"));
        Assert.Equal(0, Link(Path.Combine(home.PhysicalRoot, "alias"), Path.Combine(working.PhysicalRoot, "cross-root")));
        await Assert.ThrowsAsync<AgentCoreException>(() => Read(working, "/working/cross-root"));
        await Assert.ThrowsAsync<AgentCoreException>(() => Read(home, "/home/alias"));
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(home.PhysicalRoot, "alias")));
    }

    [Fact]
    public async Task Ordinary_git_and_failed_shell_writes_are_visible_without_finalization()
    {
        var working = Working(); working.Ensure();
        await Write(working, "/working/README.md", "initial");
        var result = await Shell(working.PhysicalRoot,
            "git init -q && git -c user.name=Fixture -c user.email=fixture@example.invalid add README.md && git -c user.name=Fixture -c user.email=fixture@example.invalid commit -qm initial && git checkout -qb next && printf partial > failed.txt; exit 7");
        Assert.Equal(7, result);
        Assert.Equal("partial", await Read(working, "/working/failed.txt"));
        Assert.Equal("ref: refs/heads/next\n", await Read(working, "/working/.git/HEAD"));
        Assert.Contains(working.List("/working").Entries, e => e.Path == "/working/.git" && e.Directory);
        Assert.Equal(0, await Shell(working.PhysicalRoot, "git clone -q . clone && test -f clone/README.md"));
        Assert.Equal("initial", await Read(working, "/working/clone/README.md"));
    }

    [Fact]
    public async Task Large_trees_paginate_and_usage_is_sampled_while_tool_reads_and_writes_stay_bounded()
    {
        var working = Working(); working.Ensure();
        // Exceeds the old snapshot collection validity ceiling without large bytes.
        for (var n = 0; n < 8200; n++) await File.WriteAllTextAsync(Path.Combine(working.PhysicalRoot, $"file-{n:00000}"), "x");
        var first = working.List("/working", limit: 256);
        Assert.Equal(256, first.Entries.Count); Assert.True(first.Truncated);
        var next = working.List("/working", first.NextPath, 256);
        Assert.DoesNotContain(next.Entries, e => first.Entries.Any(f => f.Path == e.Path));
        var usage = working.Measure(); Assert.Equal(8200, usage.Bytes); Assert.Equal("sampled", usage.Enforcement);
        await Assert.ThrowsAsync<AgentCoreException>(() => working.ReadAsync("/working/file-00000", 0).AsTask());
        await Assert.ThrowsAsync<AgentCoreException>(() => working.WriteAsync("/working/excess", "xx"u8.ToArray(), 1, 10000).AsTask());
        Assert.Throws<AgentCoreException>(() => working.Delete("/working", true));
        Assert.Throws<AgentCoreException>(() => working.Stat("/working/../foreign"));
        Assert.Throws<AgentCoreException>(() => working.Stat("/etc/passwd"));
    }

    [Fact]
    public async Task Nested_file_discovery_continuations_do_not_skip_siblings_around_subtree_prefixes()
    {
        var home = Home; home.Ensure();
        string[] paths = ["/home/a.txt", "/home/a/child.txt", "/home/a0/child.txt", "/home/a_/child.txt", "/home/z.md"];
        foreach (var path in paths) await Write(home, path, "present");
        var actual = new List<string>(); string? cursor = null;
        do
        {
            var page = home.FindFiles("", cursor, 1, default);
            actual.AddRange(page.Entries.Select(entry => entry.Path)); cursor = page.NextPath;
        } while (cursor is not null);
        Assert.Equal(paths.Order(StringComparer.Ordinal), actual);
        Assert.Equal("/home/z.md", Assert.Single(home.FindFiles("markdown", null, 10, default).Entries).Path);
    }

    private static async Task<int> Shell(string directory, string command)
    {
        var info = new ProcessStartInfo("/bin/sh") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("-c"); info.ArgumentList.Add(command);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); await stdout; var error = await stderr;
        Assert.DoesNotContain("fatal:", error);
        return process.ExitCode;
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true)] private static extern int Link(string source, string destination);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
