using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Workspaces;

namespace AgentCore.Application.Tests;

public sealed class SessionWorkspaceLifecycleTests
{
    [Fact]
    public async Task Create_is_lazy_and_lifecycle_preserves_workspace_until_durable_delete()
    {
        var root = Path.Combine(Path.GetTempPath(), "agent-core-ws-life", Guid.NewGuid().ToString("N"));
        var templates = Path.Combine(root, "templates");
        Directory.CreateDirectory(templates);
        var workspace = new FileSessionWorkspace(root, templates);
        var manager = CreateManager(new InMemoryMemoryStore(), workspace);
        var created = await manager.CreateAsync("examiner", 1, SessionMode.Text);
        var physical = Path.Combine(root, created.SessionId.ToString("N"));
        Assert.False(Directory.Exists(physical));

        await manager.WriteWorkspaceAsync(created.SessionId, "/workspace/working/note.txt", "keep"u8.ToArray());
        Assert.True(File.Exists(Path.Combine(physical, "workspace", "working", "note.txt")));

        await manager.DeactivateAsync(created.SessionId);
        Assert.True(File.Exists(Path.Combine(physical, "workspace", "working", "note.txt")));

        await manager.ArchiveAsync(created.SessionId);
        Assert.True(File.Exists(Path.Combine(physical, "workspace", "working", "note.txt")));
        var archivedWrite = await Assert.ThrowsAsync<AgentCoreException>(
            () => manager.WriteWorkspaceAsync(created.SessionId, "/workspace/working/note.txt", "x"u8.ToArray()));
        Assert.Equal("SessionArchived", archivedWrite.Code);

        await manager.UnarchiveAsync(created.SessionId);
        var reopened = await manager.ReopenAsync(created.SessionId);
        Assert.Equal(2, reopened.RuntimeEpoch);
        var kept = await manager.ReadWorkspaceAsync(created.SessionId, "/workspace/working/note.txt");
        Assert.Equal("keep"u8.ToArray(), kept.Bytes);

        await manager.DurablyDeleteAsync(created.SessionId, reopened.Revision);
        Assert.False(Directory.Exists(physical));
        var missing = await Assert.ThrowsAsync<AgentCoreException>(
            () => manager.WriteWorkspaceAsync(created.SessionId, "/workspace/working/late.txt", "no"u8.ToArray()));
        Assert.Equal("NotFound", missing.Code);
    }

    private static SessionManager CreateManager(IMemoryStore store, ISessionWorkspace workspace)
    {
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 16).Select(index => Guid.Parse($"019944af-0003-7000-8000-{index:D12}")),
            Enumerable.Range(1, 8).Select(index => Guid.Parse($"873f07d1-e264-4c81-a31b-7e59e940b8{index:D2}")).ToArray());
        return new SessionManager(
            new StaticDefinitions(SampleDefinitions.Examiner),
            store,
            ids,
            TimeProvider.System,
            new VoiceAvailability { SpeechAdaptersResolved = true },
            workspace: workspace);
    }

    private sealed class StaticDefinitions(AgentDefinition definition) : IAgentDefinitionStore
    {
        public ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AgentDefinition>>([definition]);

        public ValueTask<AgentDefinition?> GetAsync(
            string id,
            int? version = null,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<AgentDefinition?>(
                string.Equals(id, definition.Id, StringComparison.Ordinal) ? definition : null);
    }
}
