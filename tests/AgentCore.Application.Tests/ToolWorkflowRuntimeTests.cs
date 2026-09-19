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

public sealed class ToolWorkflowRuntimeTests
{
    [Fact]
    public async Task Support_workflow_returns_markdown_and_authorized_artifact()
    {
        await using var runtime = await CreateSupportAsync();
        await runtime.Runtime.AttachAsync();
        Assert.True(await runtime.Runtime.SubmitUserTextAsync("Run the support case for order 91."));
        await runtime.Runtime.WaitUntilIdleAsync();
        Assert.Contains(
            runtime.Output.Items,
            item => item.Payload is StateChangedOutput state
                && state.OutputState == nameof(OutputActivity.RunningTools));
        Assert.Contains(
            runtime.Output.Items,
            item => item.Payload is StateChangedOutput state
                && state.OutputState == nameof(OutputActivity.Idle));
        var assistant = runtime.Runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant);
        Assert.Contains("delayed", assistant.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(assistant.Envelope!.Blocks, block => block.Kind == ResponseBlockKind.Markdown);
        var artifact = Assert.Single(assistant.Envelope.Blocks, block => block.Kind == ResponseBlockKind.ArtifactReference);
        Assert.False(string.Equals(artifact.ArtifactId, FixtureArtifactReferenceAuthorizer.AuthorizedId, StringComparison.Ordinal));
        Assert.True(Guid.TryParse(artifact.ArtifactId, out var artifactId));
        Assert.NotNull(await runtime.Artifacts.GetAsync(runtime.Runtime.SessionId, artifactId));
    }

    [Fact]
    public async Task Compliance_workflow_cites_retention_with_markdown_and_artifact()
    {
        await using var runtime = await CreateSupportAsync(compliance: true);
        await runtime.Runtime.AttachAsync();
        Assert.True(await runtime.Runtime.SubmitUserTextAsync("Cite retention for this compliance case."));
        await runtime.Runtime.WaitUntilIdleAsync();
        var assistant = runtime.Runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant);
        Assert.Contains("demonstration session", assistant.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("compliance-retention@demo", assistant.Text, StringComparison.Ordinal);
        Assert.Contains(
            runtime.Output.Items,
            item => item.Payload is StateChangedOutput state
                && state.OutputState == nameof(OutputActivity.Idle));
        Assert.Contains(assistant.Envelope!.Blocks, block => block.Kind == ResponseBlockKind.Markdown);
        var artifactBlock = Assert.Single(assistant.Envelope.Blocks, block => block.Kind == ResponseBlockKind.ArtifactReference);
        Assert.True(Guid.TryParse(artifactBlock.ArtifactId, out var artifactId));
        await using var stream = await runtime.Artifacts.OpenContentAsync(runtime.Runtime.SessionId, artifactId);
        using var reader = new StreamReader(stream);
        var artifactText = await reader.ReadToEndAsync();
        Assert.Contains("demonstration session", artifactText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("compliance-retention@demo", artifactText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tool_step_cap_fails_closed()
    {
        await using var runtime = await CreateSupportAsync(alwaysToolCall: true);
        await runtime.Runtime.AttachAsync();
        Assert.True(await runtime.Runtime.SubmitUserTextAsync("Run the support case for order 91."));
        await runtime.Runtime.WaitUntilIdleAsync();
        var assistant = runtime.Runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal(EntryStatus.Failed, assistant.Status);
    }

    [Fact]
    public async Task Deactivation_rejects_stale_workspace_write()
    {
        var workspace = new GatedWorkspace();
        var definition = await Load("customer-support");
        var artifacts = new InMemoryArtifactStore(TimeProvider.System);
        var knowledge = new RoleKnowledgeService(new FileApprovedKnowledgeCatalog(FindAgents()), TimeProvider.System);
        var tools = new SessionToolExecutor(knowledge, workspace: workspace, artifacts: artifacts);
        var model = new WorkspaceWriteLanguageModel();
        var output = new CapturingSessionOutput();
        await using var runtime = CreateRuntime(output, definition, model, tools, artifacts);
        await runtime.AttachAsync();
        Assert.True(await runtime.SubmitUserTextAsync("Run the support case for order 91."));
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await workspace.Entered.WaitAsync(wait.Token);
        Assert.True(await runtime.RequestDeactivateAsync());
        workspace.Release();
        await runtime.WaitUntilIdleAsync();
        Assert.Empty(workspace.Writes);
    }

    private static async Task<Harness> CreateSupportAsync(bool compliance = false, bool alwaysToolCall = false)
    {
        var definition = await Load(compliance ? "compliance" : "customer-support");
        var artifacts = new InMemoryArtifactStore(TimeProvider.System);
        var knowledge = new RoleKnowledgeService(new FileApprovedKnowledgeCatalog(FindAgents()), TimeProvider.System);
        var tools = new SessionToolExecutor(knowledge, artifacts: artifacts);
        var model = new ScriptedLanguageModel(alwaysToolCall: alwaysToolCall);
        var output = new CapturingSessionOutput();
        var runtime = CreateRuntime(output, definition, model, tools, artifacts);
        return new Harness(runtime, output, artifacts);
    }

    private static SessionRuntime CreateRuntime(
        ISessionOutput output,
        AgentDefinition definition,
        ILanguageModel model,
        SessionToolExecutor tools,
        IArtifactStore artifacts)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 128).Select(index => Guid.Parse($"019944af-00a1-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940bf01")]);
        var store = new InMemoryMemoryStore();
        var now = time.GetUtcNow();
        var snapshot = new SessionSnapshot(
            1,
            ids.NewSessionId(),
            1,
            definition,
            SessionMode.Text,
            null,
            SessionStatus.Created,
            [],
            string.Empty,
            0,
            null,
            null,
            now,
            now);
        store.SaveAsync(snapshot, 0).AsTask().GetAwaiter().GetResult();
        return new SessionRuntime(
            snapshot,
            model,
            new DefaultAgentBrain(new PromptContextBuilder()),
            store,
            output,
            ids,
            time,
            NullLogger<SessionRuntime>.Instance,
            artifacts: new SessionArtifactAuthorizer(artifacts),
            tools: tools);
    }

    private static async Task<AgentDefinition> Load(string id)
    {
        var store = new FileAgentDefinitionStore(FindAgents(), SyntheticProviderAliases.Default);
        return (await store.GetAsync(id, 1))!;
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

    private sealed record Harness(SessionRuntime Runtime, CapturingSessionOutput Output, IArtifactStore Artifacts)
        : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Runtime.DisposeAsync();
    }

    private sealed class GatedWorkspace : ISessionWorkspace
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;
        public List<string> Writes { get; } = [];

        public void Release() => _release.TrySetResult();

        public ValueTask EnsureAsync(Guid sessionId, AgentDefinition definition, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<WorkspaceNode>> ListAsync(
            Guid sessionId,
            AgentDefinition definition,
            string prefix,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<WorkspaceNode>>([]);

        public ValueTask<WorkspaceContent> ReadAsync(
            Guid sessionId,
            AgentDefinition definition,
            string logicalPath,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async ValueTask WriteAsync(
            Guid sessionId,
            string logicalPath,
            ReadOnlyMemory<byte> bytes,
            CancellationToken cancellationToken = default)
        {
            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Writes.Add(logicalPath);
        }

        public ValueTask DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class WorkspaceWriteLanguageModel : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: true);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Messages.Any(message => message.Role == ModelRole.Tool))
            {
                yield return new ModelTextDelta("done");
                yield return new ModelCompleted(ModelStopReason.Completed);
                yield break;
            }

            yield return new ModelToolCallEvent(new ModelToolCall(
                "call-w",
                ToolCatalog.WorkspaceWrite,
                """{"path":"/workspace/working/note.txt","content":"late"}"""));
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
        }
    }
}
