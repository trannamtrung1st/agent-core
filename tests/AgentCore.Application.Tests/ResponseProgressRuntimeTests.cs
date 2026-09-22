using AgentCore.Application.Agents;
using AgentCore.Application.Events;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Attachments;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.Synthetic;
using AgentCore.Infrastructure.Workspaces;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class ResponseProgressRuntimeTests
{
    [Fact]
    public async Task Attachment_progress_starts_and_completes_without_entering_history()
    {
        RuntimeTelemetry.Reset();
        RuntimeTelemetry.Configure(64, contentLogging: false);
        var attachments = new InMemoryAttachmentStore(TimeProvider.System);
        var output = new CapturingSessionOutput();
        await using var runtime = CreateExaminerRuntime(output, attachments, new AttachmentProcessor(attachments));
        var uploaded = await attachments.UploadPendingAsync(
            runtime.SessionId,
            "notes.txt",
            "text/plain",
            new MemoryStream("alpha"u8.ToArray()),
            false);
        Assert.True(await runtime.SubmitUserTextAsync("Please summarize.", attachmentIds: [uploaded.AttachmentId]));
        await runtime.WaitUntilIdleAsync();

        var progress = Progress(output);
        Assert.Contains(
            progress,
            item => item.Kind == ResponseProgressKind.ReadingAttachments && item.State == ResponseProgressState.Started);
        Assert.Contains(
            progress,
            item => item.Kind == ResponseProgressKind.ReadingAttachments && item.State == ResponseProgressState.Completed);
        Assert.All(progress, item => Assert.Equal(ResponseProgressMessages.ReadingAttachments, item.Message));
        Assert.Contains(
            output.Items,
            item => item.Payload is StateChangedOutput state
                && state.OutputState == nameof(OutputActivity.ProcessingAttachments));
        Assert.DoesNotContain(
            runtime.Snapshot.Entries,
            entry => entry.Text.Contains("Reading attachments", StringComparison.Ordinal));
        Assert.DoesNotContain(progress, item => item.Kind is ResponseProgressKind.Preparing or ResponseProgressKind.Finalizing);
        var progressTimeline = RuntimeTelemetry.SnapshotTimeline()
            .Where(item => item.Stage.StartsWith("agent.progress", StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(progressTimeline);
        Assert.All(progressTimeline, item =>
        {
            Assert.DoesNotContain("alpha", item.Detail ?? "", StringComparison.Ordinal);
            Assert.DoesNotContain("notes.txt", item.Detail ?? "", StringComparison.Ordinal);
            Assert.DoesNotContain("Reading attachments", item.Detail ?? "", StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Attachment_processing_exception_emits_failed_progress_without_sensitive_details()
    {
        var attachments = new InMemoryAttachmentStore(TimeProvider.System);
        var output = new CapturingSessionOutput();
        await using var runtime = CreateExaminerRuntime(
            output,
            attachments,
            new ThrowingProcessor("secret-token=abc"));
        var uploaded = await attachments.UploadPendingAsync(
            runtime.SessionId,
            "notes.txt",
            "text/plain",
            new MemoryStream("alpha"u8.ToArray()),
            false);
        Assert.True(await runtime.SubmitUserTextAsync("Please summarize.", attachmentIds: [uploaded.AttachmentId]));
        await runtime.WaitUntilIdleAsync();

        var failed = Assert.Single(
            Progress(output),
            item => item.Kind == ResponseProgressKind.ReadingAttachments && item.State == ResponseProgressState.Failed);
        Assert.Equal(ResponseProgressMessages.ReadingAttachments, failed.Message);
        Assert.DoesNotContain("secret-token", failed.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("alpha", string.Join('\n', Progress(output).Select(item => item.Message)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tool_progress_uses_runtime_operation_id_not_provider_tool_id()
    {
        await using var runtime = await CreateSupportAsync();
        await runtime.Runtime.AttachAsync();
        Assert.True(await runtime.Runtime.SubmitUserTextAsync("Run the support case for order 91."));
        await runtime.Runtime.WaitUntilIdleAsync();

        var toolProgress = Progress(runtime.Output)
            .Where(item => item.Kind == ResponseProgressKind.RunningTool)
            .ToArray();
        Assert.Contains(toolProgress, item => item.State == ResponseProgressState.Started && item.OperationId is not null);
        Assert.Contains(toolProgress, item => item.State == ResponseProgressState.Completed && item.OperationId is not null);
        Assert.All(toolProgress, item =>
        {
            Assert.NotNull(item.OperationId);
            Assert.NotEqual(Guid.Empty, item.OperationId);
            Assert.Equal(ResponseProgressMessages.RunningTools, item.Message);
            Assert.DoesNotContain("identity", item.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("call-1", item.Message, StringComparison.Ordinal);
        });
        Assert.Contains(
            runtime.Output.Items,
            item => item.Payload is StateChangedOutput state && state.OutputState == nameof(OutputActivity.RunningTools));
        Assert.Contains(
            runtime.Output.Items,
            item => item.Payload is StateChangedOutput state && state.OutputState == nameof(OutputActivity.AgentGenerating));
        Assert.DoesNotContain(
            runtime.Runtime.Snapshot.Entries,
            entry => entry.Text.Contains("Running tools", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Multiple_tools_replace_progress_instead_of_becoming_history()
    {
        var definition = await Load("customer-support");
        var output = new CapturingSessionOutput();
        var artifacts = new InMemoryArtifactStore(TimeProvider.System);
        var tools = new SessionToolExecutor(
            new RoleKnowledgeService(new FileApprovedKnowledgeCatalog(FindAgents()), TimeProvider.System),
            artifacts: artifacts);
        await using var runtime = CreateSupportRuntime(output, definition, new DualToolLanguageModel(), tools, artifacts);
        await runtime.AttachAsync();
        Assert.True(await runtime.SubmitUserTextAsync("Run the support case for order 91."));
        await runtime.WaitUntilIdleAsync();

        var started = Progress(output)
            .Where(item => item.Kind == ResponseProgressKind.RunningTool && item.State == ResponseProgressState.Started)
            .ToArray();
        Assert.Equal(2, started.Length);
        Assert.NotEqual(started[0].OperationId, started[1].OperationId);
        Assert.Equal(2, runtime.Snapshot.Entries.Count);
        Assert.DoesNotContain(
            runtime.Snapshot.Entries,
            entry => entry.Text.Contains("Running tools", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancellation_interruption_and_provider_failure_clear_progress()
    {
        var workspace = new GatedWorkspace();
        var definition = await Load("customer-support");
        var artifacts = new InMemoryArtifactStore(TimeProvider.System);
        var tools = new SessionToolExecutor(
            new RoleKnowledgeService(new FileApprovedKnowledgeCatalog(FindAgents()), TimeProvider.System),
            workspace: workspace,
            artifacts: artifacts);
        var output = new CapturingSessionOutput();
        await using var runtime = CreateSupportRuntime(output, definition, new WorkspaceWriteLanguageModel(), tools, artifacts);
        await runtime.AttachAsync();
        Assert.True(await runtime.SubmitUserTextAsync("Run the support case for order 91."));
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await workspace.Entered.WaitAsync(wait.Token);
        var started = await output.WaitForAsync(
            item => item.Payload is ResponseProgressOutput progress
                && progress.Kind == ResponseProgressKind.RunningTool
                && progress.State == ResponseProgressState.Started,
            wait.Token);
        Assert.NotNull(started.ResponseId);
        var cancelled = await runtime.CancelResponseAsync(started.ResponseId.Value, wait.Token);
        Assert.Equal(ResponseCancelResult.Cancelled, cancelled);
        workspace.Release();
        await runtime.WaitUntilIdleAsync();
        Assert.Contains(
            Progress(output),
            item => item.Kind == ResponseProgressKind.RunningTool && item.State == ResponseProgressState.Failed);
    }

    [Fact]
    public async Task Interrupt_clears_owned_progress()
    {
        var workspace = new GatedWorkspace();
        var definition = await Load("customer-support");
        var artifacts = new InMemoryArtifactStore(TimeProvider.System);
        var tools = new SessionToolExecutor(
            new RoleKnowledgeService(new FileApprovedKnowledgeCatalog(FindAgents()), TimeProvider.System),
            workspace: workspace,
            artifacts: artifacts);
        var output = new CapturingSessionOutput();
        await using var runtime = CreateSupportRuntime(output, definition, new WorkspaceWriteLanguageModel(), tools, artifacts);
        await runtime.AttachAsync();
        Assert.True(await runtime.SubmitUserTextAsync("first"));
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await workspace.Entered.WaitAsync(wait.Token);
        await output.WaitForAsync(
            item => item.Payload is ResponseProgressOutput progress
                && progress.Kind == ResponseProgressKind.RunningTool
                && progress.State == ResponseProgressState.Started,
            wait.Token);
        Assert.True(await runtime.SubmitUserTextAsync("second", behavior: UserTextBehavior.Interrupt));
        await output.WaitForAsync(
            item => item.Payload is ResponseProgressOutput progress && progress.State == ResponseProgressState.Failed,
            wait.Token);
        workspace.Release();
        await runtime.WaitUntilIdleAsync();
        Assert.Contains(
            runtime.Snapshot.Entries,
            entry => entry.Role == ConversationRole.Assistant && entry.Status == EntryStatus.Interrupted);
    }

    [Fact]
    public async Task Provider_failure_clears_progress_without_copying_provider_error()
    {
        var attachments = new InMemoryAttachmentStore(TimeProvider.System);
        var output = new CapturingSessionOutput();
        await using var runtime = CreateExaminerRuntime(
            output,
            attachments,
            new AttachmentProcessor(attachments),
            new ImmediateFailureLanguageModel("token=super-secret"));
        var uploaded = await attachments.UploadPendingAsync(
            runtime.SessionId,
            "notes.txt",
            "text/plain",
            new MemoryStream("alpha"u8.ToArray()),
            false);
        Assert.True(await runtime.SubmitUserTextAsync("Please summarize.", attachmentIds: [uploaded.AttachmentId]));
        await runtime.WaitUntilIdleAsync();
        Assert.DoesNotContain(
            Progress(output).Select(item => item.Message ?? string.Empty),
            message => message.Contains("super-secret", StringComparison.Ordinal));
        Assert.Contains(Progress(output), item => item.State == ResponseProgressState.Failed || item.State == ResponseProgressState.Completed);
    }

    [Fact]
    public async Task Stale_response_tool_completion_is_ignored_after_supersession()
    {
        var workspace = new GatedWorkspace();
        var definition = await Load("customer-support");
        var artifacts = new InMemoryArtifactStore(TimeProvider.System);
        var tools = new SessionToolExecutor(
            new RoleKnowledgeService(new FileApprovedKnowledgeCatalog(FindAgents()), TimeProvider.System),
            workspace: workspace,
            artifacts: artifacts);
        var output = new CapturingSessionOutput();
        await using var runtime = CreateSupportRuntime(output, definition, new WorkspaceWriteLanguageModel(), tools, artifacts);
        await runtime.AttachAsync();
        Assert.True(await runtime.SubmitUserTextAsync("first"));
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await workspace.Entered.WaitAsync(wait.Token);
        var started = await output.WaitForAsync(
            item => item.Payload is ResponseProgressOutput progress
                && progress.Kind == ResponseProgressKind.RunningTool
                && progress.State == ResponseProgressState.Started,
            wait.Token);
        var staleResponseId = started.ResponseId;
        var staleOperation = Assert.IsType<ResponseProgressOutput>(started.Payload).OperationId;
        Assert.True(await runtime.SubmitUserTextAsync("second", behavior: UserTextBehavior.Interrupt));
        await output.WaitForAsync(
            item => item.ResponseId == staleResponseId
                && item.Payload is ResponseProgressOutput progress
                && progress.State == ResponseProgressState.Failed,
            wait.Token);
        var count = output.Items.Count;
        workspace.Release();
        await runtime.WaitUntilIdleAsync();
        Assert.DoesNotContain(
            output.Items.Skip(count),
            item => item.ResponseId == staleResponseId
                && item.Payload is ResponseProgressOutput progress
                && progress.OperationId == staleOperation
                && progress.State == ResponseProgressState.Completed);
    }

    [Fact]
    public async Task Stale_epoch_after_deactivate_does_not_emit_later_tool_progress()
    {
        var workspace = new GatedWorkspace();
        var definition = await Load("customer-support");
        var artifacts = new InMemoryArtifactStore(TimeProvider.System);
        var tools = new SessionToolExecutor(
            new RoleKnowledgeService(new FileApprovedKnowledgeCatalog(FindAgents()), TimeProvider.System),
            workspace: workspace,
            artifacts: artifacts);
        var output = new CapturingSessionOutput();
        await using var runtime = CreateSupportRuntime(output, definition, new WorkspaceWriteLanguageModel(), tools, artifacts);
        await runtime.AttachAsync();
        Assert.True(await runtime.SubmitUserTextAsync("Run the support case for order 91."));
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await workspace.Entered.WaitAsync(wait.Token);
        await output.WaitForAsync(
            item => item.Payload is ResponseProgressOutput progress && progress.State == ResponseProgressState.Started,
            wait.Token);
        var before = Progress(output).Count;
        Assert.True(await runtime.RequestDeactivateAsync());
        workspace.Release();
        await runtime.WaitUntilIdleAsync();
        var after = Progress(output).Skip(before).ToArray();
        Assert.DoesNotContain(after, item => item.State is ResponseProgressState.Started or ResponseProgressState.Completed);
        Assert.Contains(Progress(output), item => item.State == ResponseProgressState.Failed);
    }

    [Fact]
    public async Task Detach_and_end_clear_attachment_progress()
    {
        var attachments = new InMemoryAttachmentStore(TimeProvider.System);
        var gate = new TaskCompletionSource<IReadOnlyList<AttachmentProcessResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new CapturingSessionOutput();
        await using var runtime = CreateExaminerRuntime(output, attachments, new GatedProcessor(gate));
        await runtime.AttachAsync();
        var uploaded = await attachments.UploadPendingAsync(
            runtime.SessionId,
            "slow.txt",
            "text/plain",
            new MemoryStream("aaa"u8.ToArray()),
            false);
        Assert.True(await runtime.SubmitUserTextAsync("first", attachmentIds: [uploaded.AttachmentId]));
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await output.WaitForAsync(
            item => item.Payload is ResponseProgressOutput progress && progress.State == ResponseProgressState.Started,
            wait.Token);
        await runtime.DetachAsync();
        gate.TrySetResult([]);
        await runtime.WaitUntilIdleAsync();
        Assert.Contains(Progress(output), item => item.State == ResponseProgressState.Failed);
    }

    [Fact]
    public async Task Progress_is_absent_from_sqlite_history_and_not_emitted_from_elapsed_time()
    {
        await using var sqlite = await SqliteTestHarness.CreateMigratedAsync();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero));
        var output = new CapturingSessionOutput();
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 64).Select(index => Guid.Parse($"019944af-00b2-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b901")]);
        var snapshot = new SessionSnapshot(
            1,
            ids.NewSessionId(),
            1,
            SampleDefinitions.Examiner,
            SessionMode.Text,
            null,
            SessionStatus.Created,
            [],
            string.Empty,
            0,
            null,
            null,
            time.GetUtcNow(),
            time.GetUtcNow());
        await sqlite.Store.SaveAsync(snapshot, 0);
        await using var runtime = new SessionRuntime(
            snapshot,
            new ScriptedLanguageModel(),
            new DefaultAgentBrain(new PromptContextBuilder()),
            sqlite.Store,
            output,
            ids,
            time,
            NullLogger<SessionRuntime>.Instance);
        Assert.True(await runtime.SubmitUserTextAsync("Hello"));
        time.Advance(TimeSpan.FromSeconds(45));
        await runtime.WaitUntilIdleAsync();
        Assert.DoesNotContain(
            Progress(output),
            item => item.Kind is ResponseProgressKind.Preparing or ResponseProgressKind.Finalizing or ResponseProgressKind.WaitingExternal);
        var loaded = await sqlite.Store.LoadAsync(runtime.SessionId);
        Assert.NotNull(loaded);
        Assert.DoesNotContain(
            loaded!.Entries,
            entry => entry.Text.Contains("attachments", StringComparison.OrdinalIgnoreCase)
                || entry.Text.Contains("Running tools", StringComparison.Ordinal));
        var history = await sqlite.Store.ReadHistoryAsync(runtime.SessionId, 0, 50);
        Assert.DoesNotContain(
            history,
            entry => entry.Text.Contains("Reading attachments", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Terminal_session_end_clears_live_progress()
    {
        var workspace = new GatedWorkspace();
        var definition = await Load("customer-support");
        var artifacts = new InMemoryArtifactStore(TimeProvider.System);
        var tools = new SessionToolExecutor(
            new RoleKnowledgeService(new FileApprovedKnowledgeCatalog(FindAgents()), TimeProvider.System),
            workspace: workspace,
            artifacts: artifacts);
        var output = new CapturingSessionOutput();
        await using var runtime = CreateSupportRuntime(output, definition, new WorkspaceWriteLanguageModel(), tools, artifacts);
        await runtime.AttachAsync();
        Assert.True(await runtime.SubmitUserTextAsync("Run the support case for order 91."));
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await workspace.Entered.WaitAsync(wait.Token);
        Assert.True(await runtime.RequestEndAsync());
        workspace.Release();
        await runtime.WaitUntilIdleAsync();
        Assert.Contains(Progress(output), item => item.State == ResponseProgressState.Failed);
    }

    private static IReadOnlyList<ResponseProgressOutput> Progress(CapturingSessionOutput output) =>
        output.Items.Select(item => item.Payload).OfType<ResponseProgressOutput>().ToArray();

    private static SessionRuntime CreateExaminerRuntime(
        CapturingSessionOutput output,
        IAttachmentStore attachments,
        IAttachmentProcessor processor,
        ILanguageModel? model = null)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 64).Select(index => Guid.Parse($"019944af-00c3-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842")]);
        var store = new InMemoryMemoryStore();
        var snapshot = new SessionSnapshot(
            1,
            ids.NewSessionId(),
            1,
            SampleDefinitions.Examiner,
            SessionMode.Text,
            null,
            SessionStatus.Created,
            [],
            string.Empty,
            0,
            null,
            null,
            time.GetUtcNow(),
            time.GetUtcNow());
        store.SaveAsync(snapshot, 0).AsTask().GetAwaiter().GetResult();
        return new SessionRuntime(
            snapshot,
            model ?? new ScriptedLanguageModel(),
            new DefaultAgentBrain(new PromptContextBuilder()),
            store,
            output,
            ids,
            time,
            NullLogger<SessionRuntime>.Instance,
            attachments: attachments,
            processor: processor);
    }

    private static async Task<Harness> CreateSupportAsync()
    {
        var definition = await Load("customer-support");
        var artifacts = new InMemoryArtifactStore(TimeProvider.System);
        var tools = new SessionToolExecutor(
            new RoleKnowledgeService(new FileApprovedKnowledgeCatalog(FindAgents()), TimeProvider.System),
            artifacts: artifacts);
        var output = new CapturingSessionOutput();
        var runtime = CreateSupportRuntime(output, definition, new ScriptedLanguageModel(), tools, artifacts);
        return new Harness(runtime, output);
    }

    private static SessionRuntime CreateSupportRuntime(
        CapturingSessionOutput output,
        AgentDefinition definition,
        ILanguageModel model,
        SessionToolExecutor tools,
        IArtifactStore artifacts)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 128).Select(index => Guid.Parse($"019944af-00d4-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940bf02")]);
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

    private sealed record Harness(SessionRuntime Runtime, CapturingSessionOutput Output) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Runtime.DisposeAsync();
    }

    private sealed class GatedProcessor(TaskCompletionSource<IReadOnlyList<AttachmentProcessResult>> gate) : IAttachmentProcessor
    {
        public string Version => "gate";

        public async ValueTask<IReadOnlyList<AttachmentProcessResult>> ProcessTurnAsync(
            Guid sessionId,
            IReadOnlyList<Guid> attachmentIds,
            CancellationToken cancellationToken = default) =>
            await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class ThrowingProcessor(string secret) : IAttachmentProcessor
    {
        public string Version => "throw";

        public ValueTask<IReadOnlyList<AttachmentProcessResult>> ProcessTurnAsync(
            Guid sessionId,
            IReadOnlyList<Guid> attachmentIds,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(secret);
    }

    private sealed class ImmediateFailureLanguageModel(string secret) : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(StreamingText: true, Cancellation: true);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelFailed(new ProviderFailure(ProviderErrorCode.Unavailable, secret));
        }
    }

    private sealed class DualToolLanguageModel : ILanguageModel
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
                "call-1",
                ToolCatalog.KnowledgeRetrieve,
                """{"identity":"support-order-policy"}"""));
            yield return new ModelToolCallEvent(new ModelToolCall(
                "call-2",
                ToolCatalog.KnowledgeRetrieve,
                """{"identity":"support-order-policy"}"""));
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
        }
    }

    private sealed class GatedWorkspace : ISessionWorkspace
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

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
        }

        public ValueTask<WorkspacePatchResult> PatchTextAsync(
            Guid sessionId,
            AgentDefinition definition,
            string logicalPath,
            string expectedSha256Hex,
            IReadOnlyList<WorkspaceTextEdit> edits,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask MoveAsync(
            Guid sessionId,
            string sourceLogicalPath,
            string destinationLogicalPath,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

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
