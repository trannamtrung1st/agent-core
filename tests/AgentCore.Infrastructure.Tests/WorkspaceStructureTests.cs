using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;

public sealed class WorkspaceStructureTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task Restructure_exact_tree_empty_directories_and_ordered_batch(int profile)
    {
        using var f = await Fixture.Create(profile);
        await f.Write("project/text.txt", "hello"u8.ToArray());
        byte[] binary = [0, 255, 128, 13, 10]; await f.Write("project/sub/raw.bin", binary);
        await f.Run(new WorkspaceStructuralOperation("mkdir", Path:f.P("project/empty/deep")));
        var copied = await f.Run(new WorkspaceStructuralOperation("copy", Source:f.P("project"), Destination:f.P("copies/project")));
        Assert.True(copied.Completed); Assert.Equal(2, copied.Results[0].FilesAffected);
        Assert.Equal(binary, await f.Read("copies/project/sub/raw.bin"));
        Assert.Equal("hello"u8.ToArray(), await f.Read("copies/project/text.txt"));
        Assert.Contains(f.P("copies/project/empty/deep"), await f.Paths());
        var moved = await f.Run(new WorkspaceStructuralOperation("move", Source:f.P("copies/project"), Destination:f.P("renamed")));
        Assert.True(moved.Completed); Assert.DoesNotContain(f.P("copies/project"), await f.Paths());
        Assert.Equal(binary, await f.Read("renamed/sub/raw.bin"));
        var batch = await f.Run(new WorkspaceStructuralOperation("mkdir", Path:f.P("work")),
            new("move", Source:f.P("renamed/text.txt"), Destination:f.P("work/notes.txt")),
            new("copy", Source:f.P("renamed/sub/raw.bin"), Destination:f.P("work/raw.bin")),
            new("delete", Path:f.P("renamed"), Recursive:true));
        Assert.True(batch.Completed); Assert.Equal(4, batch.CompletedCount); Assert.Null(batch.FailedIndex);
        Assert.Equal(binary, await f.Read("work/raw.bin"));
        Assert.Equal("hello"u8.ToArray(), await f.Read("work/notes.txt"));
        Assert.DoesNotContain(f.P("renamed"), await f.Paths());
        Assert.Equal("alreadyExists", (await f.Run(new WorkspaceStructuralOperation("mkdir", Path:f.P("work")))).Results[0].Status);
        await f.Run(new WorkspaceStructuralOperation("delete", Path:f.P("project/empty/deep")));
        await f.Run(new WorkspaceStructuralOperation("delete", Path:f.P("project/text.txt")));
        Assert.DoesNotContain(f.P("project/text.txt"), await f.Paths());
        if (profile == 2) { f.Reopen(); Assert.Contains(f.P("project/empty"), await f.Paths()); Assert.Equal(binary, await f.Read("work/raw.bin")); }
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task Entire_preflight_rejects_conflicts_cycles_scope_and_quota_before_changes(int profile)
    {
        using var f = await Fixture.Create(profile, 12);
        await f.Write("source.txt", "12345678"u8.ToArray());
        var initial = await f.Paths();
        foreach (var ops in new WorkspaceStructuralOperation[][] {
            [new("mkdir",Path:f.P("created")), new("move",Source:f.P("missing"),Destination:f.P("new"))],
            [new("copy",Source:f.P("source.txt"),Destination:f.P("copy.txt"))],
            [new("mkdir",Path:f.P("created")),new("mkdir",Path:f.P("source.txt"))],
            [new("mkdir",Path:f.P("created")),new("move",Source:f.P("created"),Destination:f.P("created/child"))],
            [new("delete",Path:profile == 0 ? "/workspace" : "/home",Recursive:true)],
            [new("mkdir",Path:f.P("../escape"))],
            [new("move",Source:f.P("source.txt"),Destination:profile == 0 ? "/home/cross" : "/workspace/working/cross")],
            [new("copy",Source:f.P("source.txt"),Destination:f.P("source.txt"))],
            [new("copy",Source:f.P("source.txt"),Destination:f.P("SOURCE.txt"))],
            [new("delete",Path:f.P("*.txt"))] })
        {
            await Assert.ThrowsAsync<AgentCoreException>(async () => await f.Run(ops));
            Assert.Equal(initial, await f.Paths()); Assert.Equal("12345678"u8.ToArray(), await f.Read("source.txt"));
        }
        await f.Run(new WorkspaceStructuralOperation("mkdir",Path:f.P("folder")),new("move",Source:f.P("source.txt"),Destination:f.P("folder/file")));
        await Assert.ThrowsAsync<AgentCoreException>(async () => await f.Run(new WorkspaceStructuralOperation("delete",Path:f.P("folder"))));
        Assert.Equal("12345678"u8.ToArray(), await f.Read("folder/file"));
        // A deletion earlier in the batch frees cumulative capacity for a later copy.
        await f.Write("small", "abc"u8.ToArray());
        await f.Run(new WorkspaceStructuralOperation("delete",Path:f.P("folder"),Recursive:true),new("copy",Source:f.P("small"),Destination:f.P("copied")));
        Assert.Equal("abc"u8.ToArray(), await f.Read("copied"));
        await Assert.ThrowsAsync<AgentCoreException>(async () => await f.Run(Enumerable.Range(0,17).Select(i => new WorkspaceStructuralOperation("mkdir",Path:f.P("d"+i))).ToArray()));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task Execution_failure_preserves_previous_operations_and_cancellation_is_observed(int profile)
    {
        using var f = await Fixture.Create(profile);
        f.Hook((i, _) => i == 1 ? ValueTask.FromException(new IOException("host private details")) : ValueTask.CompletedTask);
        var result = await f.Run(new WorkspaceStructuralOperation("mkdir",Path:f.P("kept")),new("mkdir",Path:f.P("never")),new("mkdir",Path:f.P("also-never")));
        Assert.False(result.Completed); Assert.Equal(1, result.CompletedCount); Assert.Equal(1,result.FailedIndex);
        Assert.True(result.MutationsMayHaveOccurred); Assert.DoesNotContain("host private",result.Message);
        Assert.Equal(3,result.OperationCount); Assert.Equal(["completed","failed","notExecuted"],result.Results.Select(r => r.Status).ToArray());
        Assert.Contains(f.P("kept"),await f.Paths()); Assert.DoesNotContain(f.P("never"),await f.Paths());
        f.Hook(null);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await f.Run([new("mkdir",Path:f.P("cancelled"))],cancel.Token));
        Assert.DoesNotContain(f.P("cancelled"), await f.Paths());
    }

    [Theory] [InlineData(1)] [InlineData(2)]
    public async Task Home_stale_tree_cannot_replay_and_other_identity_has_no_access(int profile)
    {
        using var f = await Fixture.Create(profile);
        var token = (await f.Home!.ListAsync(f.Id,"/home",null,100)).TreeSha256!;
        await f.Write("new", "a"u8.ToArray());
        await Assert.ThrowsAsync<AgentCoreException>(async () => await f.Home.StructureAsync(f.Id,[new("mkdir",Path:"/home/stale")],token));
        var other = Guid.NewGuid(); Assert.Empty((await f.Home.ListAsync(other,"/home",null,100)).Items);
        var otherToken = (await f.Home.ListAsync(other,"/home",null,100)).TreeSha256!;
        await Assert.ThrowsAsync<AgentCoreException>(async () => await f.Home.StructureAsync(other,[new("move",Source:"/home/new",Destination:"/home/stolen")],otherToken));
        Assert.Equal("a"u8.ToArray(),await f.Read("new"));
    }

    [Fact]
    public async Task Scratch_recursive_symlink_and_foreign_session_are_denied_for_every_operation()
    {
        using var f = await Fixture.Create(0);
        await f.Write("tree/file", "safe"u8.ToArray());
        var outside = Path.Combine(f.Root,"outside"); Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside,"sentinel"),"untouched");
        Directory.CreateSymbolicLink(Path.Combine(f.Root,"scratch",f.Id.ToString("N"),"workspace","working","tree","link"),outside);
        foreach (var op in new WorkspaceStructuralOperation[] {new("copy",Source:f.P("tree"),Destination:f.P("copy")),new("move",Source:f.P("tree"),Destination:f.P("moved")),new("delete",Path:f.P("tree"),Recursive:true),new("mkdir",Path:f.P("tree/link/pwn"))})
            await Assert.ThrowsAsync<AgentCoreException>(async () => await f.Run(op));
        Assert.Equal("untouched",await File.ReadAllTextAsync(Path.Combine(outside,"sentinel")));
        Directory.Delete(Path.Combine(f.Root,"scratch",f.Id.ToString("N"),"workspace","working","tree","link"));
        await Assert.ThrowsAsync<AgentCoreException>(async () => await f.Run(new WorkspaceStructuralOperation("move",Source:f.P("tree"),Destination:f.P(Guid.NewGuid().ToString("N")+"/stolen"))));
        var other = Guid.NewGuid(); await f.Scratch!.EnsureAsync(other,Definition);
        await Assert.ThrowsAsync<AgentCoreException>(async () => await f.Scratch.StructureAsync(other,[new("delete",Path:f.P("tree"),Recursive:true)]));
        Assert.Equal("safe"u8.ToArray(),await f.Read("tree/file"));
    }

    [Theory] [InlineData(1)] [InlineData(2)]
    public async Task Home_linked_child_blob_blocks_recursive_mutation_before_any_change(int profile)
    {
        using var f = await Fixture.Create(profile); await f.Write("tree/file", "safe"u8.ToArray());
        var blob = Assert.Single(Directory.GetFiles(Path.Combine(f.Root,"blobs",f.Id.ToString("N"))));
        var outside = Path.Combine(f.Root,"sentinel"); await File.WriteAllTextAsync(outside,"untouched");
        File.Delete(blob); File.CreateSymbolicLink(blob,outside);
        var before = await f.Paths();
        await Assert.ThrowsAsync<AgentCoreException>(async () => await f.Run(new WorkspaceStructuralOperation("mkdir",Path:f.P("new")),new("delete",Path:f.P("tree"),Recursive:true)));
        Assert.Equal(before,await f.Paths());Assert.Equal("untouched",await File.ReadAllTextAsync(outside));
    }

    [Fact]
    public async Task Scratch_structure_serializes_write_and_session_teardown_cancels_pending_work()
    {
        using var f = await Fixture.Create(0); await f.Write("file","old"u8.ToArray());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Hook(async (_,ct) => {entered.TrySetResult();await release.Task.WaitAsync(ct);});
        var operation = f.Run(new WorkspaceStructuralOperation("move",Source:f.P("file"),Destination:f.P("moved")));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var write = f.Write("next","new"u8.ToArray()); Assert.False(write.IsCompleted);
        release.SetResult(); Assert.True((await operation).Completed); await write;
        Assert.Equal("old"u8.ToArray(),await f.Read("moved"));
        entered = new(TaskCreationOptions.RunContinuationsAsynchronously); release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = f.Run(new WorkspaceStructuralOperation("mkdir",Path:f.P("cancelled")));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await f.Scratch!.DeleteSessionAsync(f.Id);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        await Assert.ThrowsAsync<AgentCoreException>(async () => await f.Write("late","x"u8.ToArray()));
    }

    [Fact]
    public async Task Session_teardown_cancels_a_read_which_holds_the_shared_filesystem_gate()
    {
        using var f = await Fixture.Create(0); await f.Write("read", "content"u8.ToArray());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Scratch!.BeforeFilesystemRead = async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); };
        var read = f.Read("read"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await f.Scratch.DeleteSessionAsync(f.Id).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await read);
        Assert.False(Directory.Exists(Path.Combine(f.Root,"scratch",f.Id.ToString("N"))));
    }

    private static readonly AgentDefinition Definition = new(1,"examiner",1,new AgentIdentity("Alex","Examiner","Practice.","Calm"),["Practice"],"Instructions",new BehaviorPolicy("acknowledgeThenContinue",true,true),new ConversationPolicy("concise",true,"en",256),new InitiativePolicy(true,8000,30000,1,["longSilence"]),new VoiceConfiguration(true,"default",1),new ProviderPreferences("primary-llm","primary-stt","primary-tts"),new Dictionary<string,string>());
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(),"workspace-structure-tests",Guid.NewGuid().ToString("N"));
        public Guid Id { get; } = Guid.NewGuid(); public FileSessionWorkspace? Scratch; public FileAgentInstanceWorkspaceStore? Home;
        private Factory? factory; private long quota;
        public string P(string path) => (Scratch is null ? "/home/" : "/workspace/working/")+path;
        public static async Task<Fixture> Create(int profile,long quota=1000)
        {
            var f = new Fixture { quota=quota }; Directory.CreateDirectory(f.Root);
            if(profile==0) { f.Scratch=new(Path.Combine(f.Root,"scratch"),Path.Combine(f.Root,"templates"),maxWritableBytes:quota); await f.Scratch.EnsureAsync(f.Id,Definition); }
            else { if(profile==2) {f.factory=new(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={f.Root}/store.db").Options); await new SqliteMemoryStore(f.factory,TimeProvider.System).EnsureCreatedAsync();} f.Reopen(); }
            return f;
        }
        public void Reopen() => Home=new(Path.Combine(Root,"blobs"),TimeProvider.System,new SystemIdGenerator(TimeProvider.System),factory,maxFileBytes:quota,maxInstanceBytes:quota);
        public async Task Write(string path,byte[] bytes) { if(Scratch is not null) await Scratch.WriteAsync(Id,P(path),bytes); else await Home!.WriteFileAsync(Id,P(path),"application/octet-stream",bytes,null,null,null); }
        public async Task<byte[]> Read(string path) => Scratch is not null ? (await Scratch.ReadAsync(Id,Definition,P(path))).Bytes : (await Home!.ReadAsync(Id,null,P(path))).Bytes;
        public async Task<string[]> Paths() => Scratch is null ? (await Home!.ListAsync(Id,"/home",null,100)).Items.Select(i=>i.LogicalPath).Order().ToArray() : (await Enumerate("/workspace/working")).Order().ToArray();
        private async Task<List<string>> Enumerate(string path) {var result=new List<string>(); foreach(var n in await Scratch!.ListAsync(Id,Definition,path)) {result.Add(n.LogicalPath);if(n.Directory)result.AddRange(await Enumerate(n.LogicalPath));}return result;}
        public Task<WorkspaceStructureResult> Run(params WorkspaceStructuralOperation[] ops) => Run(ops,CancellationToken.None);
        public async Task<WorkspaceStructureResult> Run(WorkspaceStructuralOperation[] ops,CancellationToken ct) => Scratch is not null ? await Scratch.StructureAsync(Id,ops,ct) : await Home!.StructureAsync(Id,ops,(await Home.ListAsync(Id,"/home",null,100,ct)).TreeSha256!,ct);
        public void Hook(Func<int,CancellationToken,ValueTask>? hook) {if(Scratch is not null)Scratch.BeforeStructuralOperation=hook;else Home!.BeforeStructuralOperation=hook;}
        public void Dispose() {Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(Root,true);}
    }
    private sealed class Factory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext> {public AgentCoreDbContext CreateDbContext()=>new(options);public Task<AgentCoreDbContext>CreateDbContextAsync(CancellationToken ct=default)=>Task.FromResult(CreateDbContext());}
}
