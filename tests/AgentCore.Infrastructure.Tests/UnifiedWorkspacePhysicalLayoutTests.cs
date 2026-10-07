using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Workspaces;

namespace AgentCore.Infrastructure.Tests;

public sealed class UnifiedWorkspacePhysicalLayoutTests
{
    private static readonly AgentDefinition Definition = new(1,"examiner",1,new AgentIdentity("Alex","Examiner","Practice.","Calm"),["Practice"],"Instructions",new BehaviorPolicy("acknowledgeThenContinue",true,true),new ConversationPolicy("concise",true,"en",256),new InitiativePolicy(true,8000,30000,1,["longSilence"]),new VoiceConfiguration(true,"default",1),new ProviderPreferences("primary-llm","primary-stt","primary-tts"),new Dictionary<string,string>());

    [Fact]
    public async Task Trusted_owner_layout_isolates_sessions_and_agents_and_deletion_preserves_home_and_siblings()
    {
        using var f = new Fixture();
        var owner = Guid.NewGuid(); var other = Guid.NewGuid();
        var a = await f.Session(owner); var b = await f.Session(owner); var c = await f.Session(other);
        // Write/structure/transfer and sandbox path resolution must work before Ensure, using durable metadata.
        await f.Scratch.WriteAsync(a, "/workspace/working/a.txt", "A"u8.ToArray());
        await f.Scratch.StructureAsync(b, [new("mkdir", Path: "/workspace/working/empty")]);
        var transfer = await f.Scratch.ExportAsync(a, "/workspace/working/a.txt");
        await f.Scratch.ImportAsync(c, "/workspace/working/copied.txt", transfer);
        foreach (var id in new[] { a, b, c }) await f.Scratch.EnsureAsync(id, Definition);
        var physical = AgentWorkspacePhysicalPaths.WorkingDirectory(f.Root, owner, a);
        Assert.Equal(physical, await f.Scratch.PhysicalWorkingDirectoryAsync(a));
        Assert.Equal("A", await File.ReadAllTextAsync(Path.Combine(physical, "a.txt")));
        Assert.False(File.Exists(Path.Combine(await f.Scratch.PhysicalWorkingDirectoryAsync(b), "a.txt")));
        Assert.Equal("A", await File.ReadAllTextAsync(Path.Combine(await f.Scratch.PhysicalWorkingDirectoryAsync(c), "copied.txt")));
        var home = new FileAgentInstanceWorkspaceStore(f.Root, TimeProvider.System, new SystemIdGenerator(TimeProvider.System));
        var item = await home.WriteFileAsync(owner, "/home/csharp/CsvTool/Program.cs", "text/plain", "opaque"u8.ToArray(), a, null, null);
        var blob = Assert.Single(Directory.GetFiles(AgentWorkspacePhysicalPaths.HomeBlobRoot(f.Root, owner)));
        Assert.True(Guid.TryParseExact(Path.GetFileName(blob), "N", out _));
        Assert.Equal("opaque", await File.ReadAllTextAsync(blob));
        Assert.Equal("opaque"u8.ToArray(), (await home.ReadAsync(owner, item.ItemId, null)).Bytes);
        Assert.Empty((await home.ListAsync(other, "/home", null, 100)).Items);
        Assert.False(Directory.Exists(Path.Combine(AgentWorkspacePhysicalPaths.AgentRoot(f.Root, owner), "home", "csharp")));
        Assert.False(Directory.Exists(Path.Combine(f.Root, a.ToString("N"))));
        Assert.False(Directory.Exists(Path.Combine(f.Root, "agent-workspaces")));
        var sessionRoot = Path.GetDirectoryName(physical)!;
        Assert.Equal(new[] { ".provisioned", "working" }, Directory.GetFileSystemEntries(sessionRoot).Select(Path.GetFileName).Order().ToArray());
        await f.Scratch.DeleteSessionAsync(a);
        Assert.False(Directory.Exists(sessionRoot));
        Assert.True(File.Exists(blob));
        Assert.True(Directory.Exists(await f.Scratch.PhysicalWorkingDirectoryAsync(b)));
        await f.Scratch.DeleteSessionAsync(b);
        await home.DeleteInstanceContentAsync(owner);
        Assert.False(Directory.Exists(AgentWorkspacePhysicalPaths.AgentRoot(f.Root, owner)));
        Assert.True(Directory.Exists(AgentWorkspacePhysicalPaths.AgentRoot(f.Root, other)));
    }

    [Fact]
    public async Task Missing_session_fails_every_direct_operation_without_creating_a_raw_root()
    {
        using var f = new Fixture(); var missing = Guid.NewGuid();
        Func<Task>[] operations = [
            () => f.Scratch.WriteAsync(missing, "/workspace/working/a", new byte[] { 1 }).AsTask(),
            () => f.Scratch.StructureAsync(missing, [new("mkdir", Path: "/workspace/working/a")]).AsTask(),
            () => f.Scratch.ExportAsync(missing, "/workspace/working/a").AsTask(),
            () => f.Scratch.ImportAsync(missing, "/workspace/working/a", new([new("", false, "application/octet-stream", new byte[] { 1 })])).AsTask(),
            () => f.Scratch.PhysicalWorkingDirectoryAsync(missing).AsTask(),
            () => f.Scratch.EnsureAsync(missing, Definition).AsTask()];
        foreach (var operation in operations)
            Assert.Equal("NotFound", (await Assert.ThrowsAsync<AgentCoreException>(operation)).Code);
        Assert.Empty(Directory.GetFileSystemEntries(f.Root));
    }

    [Theory]
    [InlineData("raw")]
    [InlineData("intermediate")]
    [InlineData("separate")]
    public void Legacy_layout_requires_explicit_reset_and_preserves_bytes(string layout)
    {
        var parent = Path.Combine(Path.GetTempPath(), "workspace-legacy-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(parent, "workspaces");
        var old = layout switch {
            "raw" => Path.Combine(root, Guid.NewGuid().ToString("N"), "workspace", "working"),
            "intermediate" => Path.Combine(AgentWorkspacePhysicalPaths.SessionRoot(root, Guid.NewGuid(), Guid.NewGuid()), "workspace", "working"),
            _ => Path.Combine(parent, "agent-workspaces", Guid.NewGuid().ToString("N")) };
        Directory.CreateDirectory(old); var sentinel = Path.Combine(old, "keep.bin"); File.WriteAllBytes(sentinel, [0, 255, 128]);
        try {
            var error = Assert.Throws<AgentCoreException>(() => new FileSessionWorkspace(root, Path.Combine(parent, "templates"), sessions: new InMemoryMemoryStore()));
            Assert.Contains("reset required", error.Message);
            Assert.Equal(new byte[] { 0, 255, 128 }, File.ReadAllBytes(sentinel));
        } finally { Directory.Delete(parent, true); }
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "unified-layout-" + Guid.NewGuid().ToString("N"), "workspaces");
        public InMemoryMemoryStore Memory { get; } = new();
        public FileSessionWorkspace Scratch { get; }
        public Fixture() => Scratch = new(Root, Path.Combine(Path.GetDirectoryName(Root)!, "templates"), sessions: Memory);
        public async Task<Guid> Session(Guid owner)
        {
            var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
            await Memory.SaveAsync(new(1, id, 1, Definition, SessionMode.Text, null, SessionStatus.Created, [], "", 0, null, null, now, now, owner), 0);
            return id;
        }
        public void Dispose() => Directory.Delete(Path.GetDirectoryName(Root)!, true);
    }
}
