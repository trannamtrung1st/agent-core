using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Workspaces;

namespace AgentCore.Application.Tests;

public sealed class ArtifactMaterializeTests
{
    [Fact]
    public async Task Materialize_copies_workspace_file_preserves_hash_and_survives_deactivate()
    {
        var root = Path.Combine(Path.GetTempPath(), "agent-core-art", Guid.NewGuid().ToString("N"));
        var workspace = new FileSessionWorkspace(Path.Combine(root, "ws"), Path.Combine(root, "tpl"));
        var attachments = new InMemoryAttachmentStore(TimeProvider.System);
        var artifacts = new InMemoryArtifactStore(TimeProvider.System);
        var manager = CreateManager(new InMemoryMemoryStore(), attachments, workspace, artifacts);
        var created = await manager.CreateAsync("examiner", 1, SessionMode.Text);
        var uploaded = await attachments.UploadPendingAsync(
            created.SessionId,
            "notes.txt",
            "text/plain",
            new MemoryStream("hello-artifact"u8.ToArray()),
            false);

        var first = await manager.MaterializeAttachmentAsync(created.SessionId, uploaded.AttachmentId);
        Assert.Equal(uploaded.Sha256Hex, first.Sha256Hex);
        Assert.Equal(uploaded.AttachmentId, first.SourceAttachmentId);
        Assert.Equal("/workspace/working/notes.txt", first.WorkspaceLogicalPath);
        var working = await manager.ReadWorkspaceAsync(created.SessionId, "/workspace/working/notes.txt");
        Assert.Equal("hello-artifact"u8.ToArray(), working.Bytes);
        await using var original = await attachments.OpenContentAsync(created.SessionId, uploaded.AttachmentId);
        using var copy = new MemoryStream();
        await original.CopyToAsync(copy);
        Assert.Equal("hello-artifact"u8.ToArray(), copy.ToArray());

        var second = await manager.MaterializeAttachmentAsync(created.SessionId, uploaded.AttachmentId);
        Assert.Equal("/workspace/working/notes-2.txt", second.WorkspaceLogicalPath);

        await manager.DeactivateAsync(created.SessionId);
        var listed = await manager.ListArtifactsAsync(created.SessionId);
        Assert.Equal(2, listed.Count);
        var authorizer = new SessionArtifactAuthorizer(artifacts);
        Assert.True(authorizer.IsAuthorized(created.SessionId, first.ArtifactId.ToString()));
        Assert.True(authorizer.IsAuthorized(created.SessionId, FixtureArtifactReferenceAuthorizer.AuthorizedId));

        await manager.DurablyDeleteAsync(created.SessionId, (await manager.GetAsync(created.SessionId)).Revision);
        var missing = await Assert.ThrowsAsync<AgentCoreException>(
            () => manager.GetArtifactAsync(created.SessionId, first.ArtifactId));
        Assert.Equal("NotFound", missing.Code);
    }

    [Fact]
    public async Task Concurrent_artifact_writes_respect_session_quota()
    {
        var session = Guid.CreateVersion7();
        var artifacts = new InMemoryArtifactStore(TimeProvider.System, maxBytesEach: 40, maxBytesSession: 50);
        var first = artifacts.CreateAsync(session, "a.bin", "application/octet-stream", new byte[40], null, null);
        var second = artifacts.CreateAsync(session, "b.bin", "application/octet-stream", new byte[40], null, null);
        var results = await Task.WhenAll(Capture(first), Capture(second));
        Assert.Contains(results, result => result is null);
        Assert.Contains(results, result => result is { Code: "ArtifactQuotaExceeded" });
    }

    private static async Task<AgentCoreException?> Capture(ValueTask<ArtifactRecord> task)
    {
        try
        {
            await task.ConfigureAwait(false);
            return null;
        }
        catch (AgentCoreException ex)
        {
            return ex;
        }
    }

    private static SessionManager CreateManager(
        IMemoryStore store,
        IAttachmentStore attachments,
        ISessionWorkspace workspace,
        IArtifactStore artifacts)
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
            attachments,
            workspace: workspace,
            artifacts: artifacts);
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
