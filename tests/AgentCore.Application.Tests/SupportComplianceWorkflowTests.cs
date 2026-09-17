using AgentCore.Application.Agents;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.Synthetic;
using AgentCore.Infrastructure.Workspaces;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class SupportComplianceWorkflowTests
{
    [Fact]
    public async Task Support_and_compliance_chats_keep_attachments_workspace_and_artifacts_across_deactivate_reopen()
    {
        using var dir = new TempDir();
        var store = new InMemoryMemoryStore();
        var attachments = new InMemoryAttachmentStore(TimeProvider.System);
        var artifacts = new InMemoryArtifactStore(TimeProvider.System);
        var workspace = new FileSessionWorkspace(dir.WorkspaceRoot, dir.TemplateRoot, attachments);
        var manager = CreateManager(store, attachments, workspace, artifacts);
        var catalog = new Dictionary<string, Guid>();

        foreach (var (agentId, prompt) in new[]
                 {
                     ("customer-support", "Run the support case for order 91."),
                     ("compliance", "Cite retention for this compliance case.")
                 })
        {
            var created = await manager.CreateAsync(agentId, 1, SessionMode.Text);
            catalog[agentId] = created.SessionId;
            var uploaded = await attachments.UploadPendingAsync(
                created.SessionId,
                "brief.txt",
                "text/plain",
                new MemoryStream("case-file"u8.ToArray()),
                false);
            var materialized = await manager.MaterializeAttachmentAsync(created.SessionId, uploaded.AttachmentId);
            await using var runtime = CreateRuntime(
                created,
                store,
                attachments,
                artifacts,
                knowledge: new RoleKnowledgeService(new FileApprovedKnowledgeCatalog(FindAgents()), TimeProvider.System),
                workspace);
            await runtime.AttachAsync();
            Assert.True(await runtime.SubmitUserTextAsync(prompt, attachmentIds: [uploaded.AttachmentId]));
            await runtime.WaitUntilIdleAsync();
            var assistant = runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant);
            Assert.Contains(assistant.Envelope!.Blocks, block => block.Kind == ResponseBlockKind.Markdown);
            var artifactBlock = Assert.Single(assistant.Envelope.Blocks, block => block.Kind == ResponseBlockKind.ArtifactReference);
            Assert.True(Guid.TryParse(artifactBlock.ArtifactId, out var toolArtifactId));
            Assert.NotNull(await artifacts.GetAsync(created.SessionId, toolArtifactId));
            if (agentId == "compliance")
            {
                Assert.Contains("demonstration session", assistant.Text, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("compliance-retention@demo", assistant.Text, StringComparison.Ordinal);
                await using var artifactStream = await artifacts.OpenContentAsync(created.SessionId, toolArtifactId);
                using var artifactReader = new StreamReader(artifactStream);
                var artifactText = await artifactReader.ReadToEndAsync();
                Assert.Contains("demonstration session", artifactText, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("compliance-retention@demo", artifactText, StringComparison.Ordinal);
            }
            else
            {
                Assert.Contains("Delayed", assistant.Text, StringComparison.OrdinalIgnoreCase);
            }
            Assert.Equal(AttachmentState.Bound, (await attachments.GetAsync(created.SessionId, uploaded.AttachmentId))!.State);

            await manager.DeactivateAsync(created.SessionId);
            var reopened = await manager.ReopenAsync(created.SessionId);
            Assert.True(reopened.RuntimeEpoch > created.RuntimeEpoch);
            Assert.Equal(AttachmentState.Bound, (await attachments.GetAsync(created.SessionId, uploaded.AttachmentId))!.State);
            var working = await manager.ReadWorkspaceAsync(created.SessionId, "/workspace/working/brief.txt");
            Assert.Equal("case-file"u8.ToArray(), working.Bytes);
            Assert.NotNull(await artifacts.GetAsync(created.SessionId, materialized.ArtifactId));
            Assert.NotNull(await artifacts.GetAsync(created.SessionId, toolArtifactId));
            var history = (await store.LoadAsync(created.SessionId))!;
            Assert.Contains(history.Entries, entry => entry.Role == ConversationRole.Assistant && entry.Envelope is not null);
        }

        var listed = await manager.ListCatalogAsync(null, 50, false);
        Assert.Contains(listed.Items, item => item.SessionId == catalog["customer-support"] && item.Definition.Id == "customer-support");
        Assert.Contains(listed.Items, item => item.SessionId == catalog["compliance"] && item.Definition.Id == "compliance");

        foreach (var sessionId in catalog.Values)
        {
            var snapshot = await manager.GetAsync(sessionId);
            var physical = Path.Combine(dir.WorkspaceRoot, sessionId.ToString("N"));
            await manager.DurablyDeleteAsync(sessionId, snapshot.Revision);
            await manager.DurablyDeleteAsync(sessionId, snapshot.Revision);
            Assert.False(Directory.Exists(physical));
            await Assert.ThrowsAsync<AgentCoreException>(() => manager.GetAsync(sessionId));
            await Assert.ThrowsAsync<AgentCoreException>(
                () => workspace.WriteAsync(sessionId, "/workspace/working/late.txt", "no"u8.ToArray()).AsTask());
            await Assert.ThrowsAsync<AgentCoreException>(
                () => artifacts.CreateAsync(sessionId, "late.md", "text/markdown", "x"u8.ToArray(), null, null).AsTask());
        }
    }

    [Fact]
    public async Task Durable_delete_retries_blob_cleanup_after_partial_workspace_failure()
    {
        using var dir = new TempDir();
        var store = new InMemoryMemoryStore();
        var attachments = new InMemoryAttachmentStore(TimeProvider.System);
        var artifacts = new InMemoryArtifactStore(TimeProvider.System);
        var inner = new FileSessionWorkspace(dir.WorkspaceRoot, dir.TemplateRoot);
        var workspace = new OnceFailingWorkspace(inner);
        var manager = CreateManager(store, attachments, workspace, artifacts);
        var created = await manager.CreateAsync("customer-support", 1, SessionMode.Text);
        await manager.WriteWorkspaceAsync(created.SessionId, "/workspace/working/note.txt", "keep"u8.ToArray());
        var physical = Path.Combine(dir.WorkspaceRoot, created.SessionId.ToString("N"));
        Assert.True(Directory.Exists(physical));
        await Assert.ThrowsAsync<IOException>(() => manager.DurablyDeleteAsync(created.SessionId, created.Revision));
        Assert.True(Directory.Exists(physical));
        await manager.DurablyDeleteAsync(created.SessionId, created.Revision);
        Assert.False(Directory.Exists(physical));
        await Assert.ThrowsAsync<AgentCoreException>(
            () => inner.WriteAsync(created.SessionId, "/workspace/working/late.txt", "no"u8.ToArray()).AsTask());
    }

    private static SessionManager CreateManager(
        IMemoryStore store,
        IAttachmentStore attachments,
        ISessionWorkspace workspace,
        IArtifactStore artifacts)
    {
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 32).Select(index => Guid.Parse($"019944af-00b1-7000-8000-{index:D12}")),
            Enumerable.Range(1, 8).Select(index => Guid.Parse($"873f07d1-e264-4c81-a31b-7e59e940c8{index:D2}")).ToArray());
        return new SessionManager(
            new FileAgentDefinitionStore(FindAgents(), SyntheticProviderAliases.Default),
            store,
            ids,
            TimeProvider.System,
            new VoiceAvailability { SpeechAdaptersResolved = true },
            attachments,
            knowledge: new RoleKnowledgeService(new FileApprovedKnowledgeCatalog(FindAgents()), TimeProvider.System),
            workspace,
            artifacts);
    }

    private static SessionRuntime CreateRuntime(
        SessionSnapshot snapshot,
        IMemoryStore store,
        IAttachmentStore attachments,
        IArtifactStore artifacts,
        RoleKnowledgeService knowledge,
        ISessionWorkspace workspace)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 128).Select(index => Guid.Parse($"019944af-00c1-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940cf01")]);
        var tools = new SessionToolExecutor(knowledge, attachments, workspace: workspace, artifacts: artifacts);
        return new SessionRuntime(
            snapshot,
            new ScriptedLanguageModel(),
            new DefaultAgentBrain(new PromptContextBuilder()),
            store,
            new CapturingSessionOutput(),
            ids,
            time,
            NullLogger<SessionRuntime>.Instance,
            attachments: attachments,
            artifacts: new SessionArtifactAuthorizer(artifacts),
            tools: tools);
    }

    private static string FindAgents()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var agents = Path.Combine(dir.FullName, "agents");
            if (Directory.Exists(agents))
            {
                return agents;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("agents/");
    }

    private sealed class OnceFailingWorkspace(ISessionWorkspace inner) : ISessionWorkspace
    {
        private int _deletes;

        public ValueTask EnsureAsync(Guid sessionId, AgentDefinition definition, CancellationToken cancellationToken = default) =>
            inner.EnsureAsync(sessionId, definition, cancellationToken);

        public ValueTask<IReadOnlyList<WorkspaceNode>> ListAsync(
            Guid sessionId,
            AgentDefinition definition,
            string prefix,
            CancellationToken cancellationToken = default) =>
            inner.ListAsync(sessionId, definition, prefix, cancellationToken);

        public ValueTask<WorkspaceContent> ReadAsync(
            Guid sessionId,
            AgentDefinition definition,
            string logicalPath,
            CancellationToken cancellationToken = default) =>
            inner.ReadAsync(sessionId, definition, logicalPath, cancellationToken);

        public ValueTask WriteAsync(
            Guid sessionId,
            string logicalPath,
            ReadOnlyMemory<byte> bytes,
            CancellationToken cancellationToken = default) =>
            inner.WriteAsync(sessionId, logicalPath, bytes, cancellationToken);

        public ValueTask DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _deletes) == 1)
            {
                throw new IOException("simulated partial workspace delete");
            }

            return inner.DeleteSessionAsync(sessionId, cancellationToken);
        }
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Root = Path.Combine(Path.GetTempPath(), "agent-core-e2e", Guid.NewGuid().ToString("N"));
            WorkspaceRoot = Path.Combine(Root, "workspaces");
            TemplateRoot = Path.Combine(Root, "templates");
            Directory.CreateDirectory(WorkspaceRoot);
            Directory.CreateDirectory(TemplateRoot);
        }

        public string Root { get; }
        public string WorkspaceRoot { get; }
        public string TemplateRoot { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, true);
            }
        }
    }
}
