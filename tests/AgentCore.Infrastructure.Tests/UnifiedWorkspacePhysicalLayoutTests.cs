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
    private static readonly AgentDefinition Definition = new(2,"examiner",1,new AgentIdentity("Alex","Examiner","Practice.","Calm"),["Practice"],"Instructions",new BehaviorPolicy("acknowledgeThenContinue",true,true),new ConversationPolicy("concise",true,"en",256),new InitiativePolicy(true,8000,30000,1,["longSilence"]),new VoiceConfiguration(true,"default",1),new ProviderPreferences("primary-llm","primary-stt","primary-tts"),new Dictionary<string,string>());

    [Fact]
    public async Task Trusted_owner_layout_isolates_sessions_and_agents_and_deletion_preserves_home_and_siblings()
    {
        using var f = new Fixture();
        var owner = Guid.NewGuid(); var other = Guid.NewGuid();
        var a = await f.Session(owner); var b = await f.Session(owner); var c = await f.Session(other);
        // Write/structure/transfer and sandbox path resolution must work before Ensure, using durable metadata.
        await f.Scratch.WriteAsync(a, "/working/a.txt", "A"u8.ToArray());
        await f.Scratch.StructureAsync(b, [new("mkdir", Path: "/working/empty")]);
        await f.Scratch.WriteAsync(c, "/working/copied.txt", "other"u8.ToArray());
        foreach (var id in new[] { a, b, c }) await f.Scratch.EnsureAsync(id, Definition);
        var physical = AgentWorkspacePhysicalPaths.WorkingDirectory(f.Root, owner, a);
        Assert.Equal(physical, await f.Scratch.PhysicalWorkingDirectoryAsync(a));
        Assert.Equal("A", await File.ReadAllTextAsync(Path.Combine(physical, "a.txt")));
        Assert.False(File.Exists(Path.Combine(await f.Scratch.PhysicalWorkingDirectoryAsync(b), "a.txt")));
        Assert.Equal("other", await File.ReadAllTextAsync(Path.Combine(await f.Scratch.PhysicalWorkingDirectoryAsync(c), "copied.txt")));
        var home = new FileAgentInstanceWorkspaceStore(f.Root, f.Instances, f.Sandboxes);
        var item = await home.WriteFileAsync(owner, "/home/csharp/CsvTool/Program.cs", "canonical"u8.ToArray());
        var file = Path.Combine(AgentWorkspacePhysicalPaths.HomeDirectory(f.Root, owner), "csharp", "CsvTool", "Program.cs");
        Assert.Equal("canonical", await File.ReadAllTextAsync(file));
        Assert.Equal("canonical"u8.ToArray(), (await home.ReadAsync(owner, item.LogicalPath)).Bytes);
        Assert.Empty((await home.ListAsync(other, "/home", null, 100)).Items);
        Assert.True(Directory.Exists(Path.Combine(AgentWorkspacePhysicalPaths.AgentRoot(f.Root, owner), "home", "csharp")));
        Assert.False(Directory.Exists(Path.Combine(f.Root, a.ToString("N"))));
        Assert.False(Directory.Exists(Path.Combine(f.Root, "agent-workspaces")));
        var sessionRoot = Path.GetDirectoryName(physical)!;
        Assert.Equal(new[] { ".provisioned", "working" }, Directory.GetFileSystemEntries(sessionRoot).Select(Path.GetFileName).Order().ToArray());
        await f.Scratch.DeleteSessionAsync(a);
        Assert.False(Directory.Exists(sessionRoot));
        Assert.True(File.Exists(file));
        Assert.True(Directory.Exists(await f.Scratch.PhysicalWorkingDirectoryAsync(b)));
        await f.Scratch.DeleteSessionAsync(b);
        await Assert.ThrowsAsync<AgentCoreException>(() => home.DeleteInstanceContentAsync(owner).AsTask());
        Assert.True(File.Exists(file));
        Assert.True(Directory.Exists(AgentWorkspacePhysicalPaths.AgentRoot(f.Root, other)));
    }

    [Fact]
    public async Task Missing_session_fails_every_direct_operation_without_creating_a_raw_root()
    {
        using var f = new Fixture(); var missing = Guid.NewGuid();
        Func<Task>[] operations = [
            () => f.Scratch.WriteAsync(missing, "/working/a", new byte[] { 1 }).AsTask(),
            () => f.Scratch.StructureAsync(missing, [new("mkdir", Path: "/working/a")]).AsTask(),
            () => f.Scratch.CopyAcrossScopesAsync(missing, "/working/a", "/home/a").AsTask(),
            () => f.Scratch.PhysicalWorkingDirectoryAsync(missing).AsTask(),
            () => f.Scratch.EnsureAsync(missing, Definition).AsTask()];
        foreach (var operation in operations)
            Assert.Equal("NotFound", (await Assert.ThrowsAsync<AgentCoreException>(operation)).Code);
        Assert.Equal(new[] { ".canonical-layout" }, Directory.GetFileSystemEntries(f.Root).Select(Path.GetFileName));
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
            _ => Path.Combine(root, "agent-"+Guid.NewGuid().ToString("N"), "home", "blobs") };
        Directory.CreateDirectory(old); var sentinel = Path.Combine(old, "keep.bin"); File.WriteAllBytes(sentinel, [0, 255, 128]);
        try {
            var error = Assert.Throws<AgentCoreException>(() => AgentWorkspacePhysicalPaths.ValidateRoot(root));
            Assert.Contains("reset required", error.Message);
            Assert.Equal(new byte[] { 0, 255, 128 }, File.ReadAllBytes(sentinel));
        } finally { Directory.Delete(parent, true); }
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "unified-layout-" + Guid.NewGuid().ToString("N"), "workspaces");
        public InMemoryMemoryStore Memory { get; } = new();
        public InMemoryAgentInstanceStore Instances { get; } = new();
        public InMemorySandboxStore Sandboxes { get; } = new();
        public FileSessionWorkspace Scratch { get; }
        public Fixture() => Scratch = new(Root, Path.Combine(Path.GetDirectoryName(Root)!, "templates"), sessions: Memory, instances: Instances, sandboxes: Sandboxes);
        public async Task<Guid> Session(Guid owner)
        {
            var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
            if (await Instances.FindAsync(owner) is null) await Instances.InsertAsync(new(owner, Definition.Id, Definition.Version, Definition.Identity, AgentInstanceLifecycle.Active, now, now));
            await Memory.SaveAsync(new(1, id, 1, Definition, SessionMode.Text, null, SessionStatus.Created, [], "", 0, null, null, now, now, owner), 0);
            return id;
        }
        public void Dispose() => Directory.Delete(Path.GetDirectoryName(Root)!, true);
    }
}
