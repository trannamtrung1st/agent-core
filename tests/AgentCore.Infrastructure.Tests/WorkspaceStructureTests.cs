using System.Text;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Workspaces;

namespace AgentCore.Infrastructure.Tests;

public sealed class WorkspaceStructureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Filesystem_operations_preserve_exact_bytes_empty_folders_and_links_without_tree_tokens(bool home)
    {
        using var f = new Fixture(home);
        await f.Write("project/text.txt", "hello"u8.ToArray());
        byte[] binary = [0,255,128,13,10]; await f.Write("project/sub/raw.bin", binary);
        Assert.True((await f.Run(new WorkspaceStructuralOperation("mkdir", Path:f.Path("project/empty/deep")))).Completed);
        Assert.True((await f.Run(new WorkspaceStructuralOperation("copy", Source:f.Path("project"), Destination:f.Path("backup")))).Completed);
        Assert.Equal(binary, await f.Read("backup/sub/raw.bin"));
        Assert.True(Directory.Exists(System.IO.Path.Combine(f.Physical, "backup", "empty", "deep")));
        File.CreateSymbolicLink(System.IO.Path.Combine(f.Physical, "backup", "link"), "text.txt");
        Assert.True((await f.Run(new WorkspaceStructuralOperation("move", Source:f.Path("backup"), Destination:f.Path("renamed")))).Completed);
        Assert.Equal("hello", Encoding.UTF8.GetString(await f.Read("renamed/link")));
        var batch = await f.Run(new WorkspaceStructuralOperation("mkdir", Path:f.Path("work")), new("move", Source:f.Path("renamed/text.txt"), Destination:f.Path("work/notes.txt")),
            new("copy", Source:f.Path("renamed/sub/raw.bin"), Destination:f.Path("work/raw.bin")), new("delete", Path:f.Path("renamed"), Recursive:true));
        Assert.True(batch.Completed); Assert.Equal(4, batch.CompletedCount);
        Assert.Equal(binary, await f.Read("work/raw.bin"));
        Assert.Equal("hello"u8.ToArray(), await f.Read("work/notes.txt"));
        Assert.False(Directory.Exists(System.IO.Path.Combine(f.Physical, "renamed")));
        Assert.True((await f.Run(new WorkspaceStructuralOperation("mkdir", Path:f.Path("work")))).Completed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Input_scope_and_protected_root_validation_precede_all_mutations(bool home)
    {
        using var f = new Fixture(home);
        var otherRoot = home ? "/working/foreign" : "/home/foreign";
        await Assert.ThrowsAsync<AgentCoreException>(() => f.Run(new WorkspaceStructuralOperation("mkdir", Path:f.Path("must-not-exist")), new("delete", Path:otherRoot)));
        Assert.False(Directory.Exists(System.IO.Path.Combine(f.Physical, "must-not-exist")));
        await Assert.ThrowsAsync<AgentCoreException>(() => f.Run(new WorkspaceStructuralOperation("delete", Path:home ? "/home" : "/working", Recursive:true)));
        await Assert.ThrowsAsync<AgentCoreException>(() => f.Run(new WorkspaceStructuralOperation("mkdir", Path:f.Path("../escape"))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ordered_batch_stops_at_actual_failure_and_reports_retained_partial_effects(bool home)
    {
        using var f = new Fixture(home); await f.Write("source", "bytes"u8.ToArray());
        var result = await f.Run(new WorkspaceStructuralOperation("mkdir", Path:f.Path("created")), new("copy", Source:f.Path("source"), Destination:f.Path("created/copy")),
            new("delete", Path:f.Path("missing")), new("mkdir", Path:f.Path("not-executed")));
        Assert.False(result.Completed); Assert.Equal(2, result.CompletedCount); Assert.Equal(2, result.FailedIndex);
        Assert.True(result.MutationsMayHaveOccurred);
        Assert.Equal(new[] {"completed","completed","failed","notExecuted"},result.Results.Select(r => r.Status));
        Assert.Equal("bytes"u8.ToArray(), await f.Read("created/copy"));
        Assert.False(Directory.Exists(System.IO.Path.Combine(f.Physical, "not-executed")));
        Assert.False((await f.Run(new WorkspaceStructuralOperation("delete", Path:f.Path("created")))).Completed);
        Assert.True((await f.Run(new WorkspaceStructuralOperation("delete", Path:f.Path("created"), Recursive:true))).Completed);
    }

    [Fact]
    public async Task Cross_scope_copy_streams_bytes_and_directories_but_never_links_owners_or_merges()
    {
        using var f = new Fixture(false);
        await f.Write("project/data", "exact"u8.ToArray());
        await f.Run(new WorkspaceStructuralOperation("mkdir", Path:f.Path("project/empty")));
        await f.Working.CopyAcrossScopesAsync(f.Session, "/working/project", "/home/shared");
        var home = new FileAgentInstanceWorkspaceStore(f.Root, f.Metadata.Instances, f.Metadata.Sandboxes);
        Assert.Equal("exact"u8.ToArray(), (await home.ReadAsync(f.Metadata.Owner, "/home/shared/data")).Bytes);
        Assert.Contains((await home.ListAsync(f.Metadata.Owner, "/home/shared", null, 100)).Items, i => i.Directory && i.LogicalPath.EndsWith("/empty"));
        await f.Working.CopyAcrossScopesAsync(f.Session, "/home/shared", "/working/copied");
        Assert.Equal("exact"u8.ToArray(), await f.Read("copied/data"));
        await Assert.ThrowsAsync<AgentCoreException>(() => f.Working.CopyAcrossScopesAsync(f.Session, "/home/shared", "/working/copied").AsTask());
    }

    [Fact]
    public async Task Deletion_cancels_outstanding_io_and_live_mounts_block_physical_purge()
    {
        using var f = new Fixture(false); await f.Write("retained", "bytes"u8.ToArray());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Working.BeforeFilesystemRead = async token => { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan,token); };
        var read = f.Read("retained"); await entered.Task;
        await f.Working.DeleteSessionAsync(f.Session);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.False(Directory.Exists(f.Physical));
        await Assert.ThrowsAsync<AgentCoreException>(() => f.Write("late", "x"u8.ToArray()));
        using var mounted = new Fixture(false); await mounted.Write("retained", "data"u8.ToArray());
        var lease = new SandboxLease(mounted.Session,mounted.Metadata.Owner,Guid.NewGuid(),1,
            new("fixture",1,"Fixture","",true,"sha256:"+new string('a',64)),SandboxLeaseState.Ready,"owned",DateTimeOffset.UtcNow,DateTimeOffset.UtcNow.AddMinutes(1));
        await mounted.Metadata.Sandboxes.SaveLeaseAsync(lease,0);
        await Assert.ThrowsAsync<AgentCoreException>(() => mounted.Working.DeleteSessionAsync(mounted.Session).AsTask());
        Assert.Equal("data"u8.ToArray(), await mounted.Read("retained"));
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "workspace-structure-"+Guid.NewGuid().ToString("N"));
        internal Guid Session { get; } = Guid.NewGuid(); internal WorkspaceTestSessions Metadata { get; } = new();
        internal FileSessionWorkspace Working { get; }
        private readonly FileAgentInstanceWorkspaceStore _home;
        private readonly bool _useHome;
        internal string Physical => _useHome ? AgentWorkspacePhysicalPaths.HomeDirectory(Root,Metadata.Owner) : AgentWorkspacePhysicalPaths.WorkingDirectory(Root,Metadata.Owner,Session);
        internal Fixture(bool home)
        {
            _useHome=home;
            Working = new(Root, Root+"-templates",sessions:Metadata,instances:Metadata.Instances,sandboxes:Metadata.Sandboxes);
            _home = new(Root,Metadata.Instances,Metadata.Sandboxes);
        }
        internal string Path(string relative) => (_useHome ? "/home/" : "/working/") + relative;
        internal async Task Write(string path, byte[] bytes)
        { if(_useHome) await _home.WriteFileAsync(Metadata.Owner,Path(path),bytes); else await Working.WriteAsync(Session,Path(path),bytes); }
        internal async Task<byte[]> Read(string path) => _useHome ? (await _home.ReadAsync(Metadata.Owner,Path(path))).Bytes
            : (await Working.ReadAsync(Session,(await Metadata.LoadAsync(Session))!.Definition,Path(path))).Bytes;
        internal async Task<WorkspaceStructureResult> Run(params WorkspaceStructuralOperation[] operations) => _useHome
            ? await _home.StructureAsync(Metadata.Owner,operations) : await Working.StructureAsync(Session,operations);
        public void Dispose() { if(Directory.Exists(Root))Directory.Delete(Root,true); }
    }
}
