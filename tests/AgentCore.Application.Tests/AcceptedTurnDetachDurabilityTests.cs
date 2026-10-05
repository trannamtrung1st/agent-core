using AgentCore.Application.Agents;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class AcceptedTurnDetachDurabilityTests
{
    [Fact]
    public async Task Disposal_with_a_queued_detach_does_not_report_a_false_persistence_failure()
    {
        var output = new AttachOutputGate();
        var logger = new ShutdownLogger();
        var runtime = CreateRuntime(output, new ScriptedLanguageModel(), new FakeTimeProvider(),
            new DefaultAgentBrain(new PromptContextBuilder()), logger: logger);
        var attach = runtime.AttachAsync();
        await output.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var detach = runtime.DetachAsync();
        var dispose = runtime.DisposeAsync().AsTask();
        output.Release.TrySetResult();
        await Task.WhenAll(attach, detach, dispose).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(logger.Errors);
    }

    private sealed class AttachOutputGate : ISessionOutput
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask PublishAsync(SessionOutput output, CancellationToken cancellationToken = default)
        {
            if (output.Payload is not ReadyOutput) return;
            Entered.TrySetResult();
            await Release.Task;
        }
    }

    private sealed class ShutdownLogger : ILogger<SessionRuntime>
    {
        public List<Exception?> Errors { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (level >= LogLevel.Error) Errors.Add(exception); }
    }

    [Fact]
    public async Task Detach_after_ack_before_response_still_executes()
    {
        var output = new CapturingSessionOutput();
        var model = new DetachHoldingLanguageModel();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        await using var runtime = CreateRuntime(
            output,
            model,
            time,
            new DefaultAgentBrain(new PromptContextBuilder()));
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello");
        await WaitUntilAsync(() => runtime.HasAcceptedConversationWorkAsync());

        await runtime.DetachAsync();
        model.Release.TrySetResult();
        await runtime.WaitUntilAcceptedConversationWorkSettledAsync();

        var assistant = runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal(EntryStatus.Completed, assistant.Status);
        Assert.False(string.IsNullOrWhiteSpace(assistant.Text));
    }

    [Fact]
    public async Task Idle_detach_still_pauses_disconnected()
    {
        var output = new CapturingSessionOutput();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        await using var runtime = CreateRuntime(
            output,
            new ScriptedLanguageModel(),
            time,
            new DefaultAgentBrain(new PromptContextBuilder()));
        await runtime.AttachAsync();
        await runtime.DetachAsync();
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(SessionStatus.Paused, runtime.Snapshot.Status);
        Assert.Equal("disconnected", runtime.Snapshot.PauseReason);
        Assert.False(await runtime.HasAcceptedConversationWorkAsync());
    }

    [Fact]
    public async Task Headless_finalizer_waits_for_terminal_assistant_persistence()
    {
        var releaseTerminalPersist = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new TerminalAssistantPersistGate(releaseTerminalPersist);
        var output = new CapturingSessionOutput();
        var model = new DetachHoldingLanguageModel();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        var sessionId = Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842");
        await using var runtime = CreateRuntime(
            output,
            model,
            time,
            new DefaultAgentBrain(new PromptContextBuilder()),
            store);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("hi");
        await WaitForActiveResponseAsync(runtime);
        await runtime.DetachAsync();
        model.Release.TrySetResult();
        await store.WaitForSaveStartedAsync(TimeSpan.FromSeconds(5));
        var inMemoryAssistant = runtime.Snapshot.Entries.Single(e => e.Role == ConversationRole.Assistant);
        Assert.Equal(EntryStatus.Completed, inMemoryAssistant.Status);
        Assert.True(await runtime.HasAcceptedConversationWorkAsync());
        using (var early = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => runtime.WaitUntilAcceptedConversationWorkSettledAsync(early.Token));
        }

        store.Release();
        await runtime.WaitUntilAcceptedConversationWorkSettledAsync();
        await runtime.FinalizeDetachedPauseAsync();
        await runtime.WaitUntilIdleAsync();

        var durable = (await store.LoadAsync(sessionId))!;
        Assert.Equal(SessionStatus.Paused, durable.Status);
        Assert.Equal(["hi"], durable.Entries.Where(e => e.Role == ConversationRole.User).Select(e => e.Text).ToArray());
        var assistants = durable.Entries.Where(e => e.Role == ConversationRole.Assistant).ToArray();
        Assert.Single(assistants);
        Assert.Equal(EntryStatus.Completed, assistants[0].Status);
        Assert.Equal("T1", assistants[0].Text);
        Assert.DoesNotContain(assistants, entry => entry.Status == EntryStatus.Streaming && string.IsNullOrEmpty(entry.Text));

        var reopened = await PausedSessionReopen.ReopenAsync(store, durable, time);
        await using var restored = CreateRuntime(
            output,
            new ScriptedLanguageModel(),
            time,
            new DefaultAgentBrain(new PromptContextBuilder()),
            store,
            reopened);
        await restored.AttachAsync();
        await restored.WaitUntilMailboxDrainedAsync();
        Assert.Equal("hi", restored.Snapshot.Entries.Single(e => e.Role == ConversationRole.User).Text);
        var restoredAssistant = restored.Snapshot.Entries.Single(e => e.Role == ConversationRole.Assistant);
        Assert.Equal(EntryStatus.Completed, restoredAssistant.Status);
        Assert.Equal("T1", restoredAssistant.Text);
    }

    [Fact]
    public async Task Reattach_during_blocked_terminal_persist_keeps_response_until_save_applies()
    {
        var releaseTerminalPersist = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new TerminalAssistantPersistGate(releaseTerminalPersist);
        var turnExecutions = new InMemoryConversationTurnExecutionStore();
        var output = new CapturingSessionOutput();
        var model = new DetachHoldingLanguageModel();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        await using var runtime = CreateRuntime(
            output,
            model,
            time,
            new DefaultAgentBrain(new PromptContextBuilder()),
            store,
            turnExecutions: turnExecutions);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("hi");
        await WaitForActiveResponseAsync(runtime);
        await runtime.DetachAsync();
        model.Release.TrySetResult();
        await store.WaitForSaveStartedAsync(TimeSpan.FromSeconds(5));
        Assert.True(runtime.HeadlessTransportDetached);
        var openBefore = await turnExecutions.ListOpenForSessionAsync(runtime.SessionId);
        Assert.Single(openBefore);
        var executionId = openBefore[0].ExecutionId;

        var attaching = runtime.AttachAsync();
        await Task.Delay(30);
        Assert.True(await runtime.HasAcceptedConversationWorkAsync());
        Assert.Equal(executionId, (await turnExecutions.ListOpenForSessionAsync(runtime.SessionId)).Single().ExecutionId);

        store.Release();
        Assert.True(await attaching);
        await WaitUntilAsync(async () => !await runtime.HasAcceptedConversationWorkAsync());
        Assert.Empty(await turnExecutions.ListOpenForSessionAsync(runtime.SessionId));
        Assert.Null(runtime.ActiveResponseId);

        var assistant = runtime.Snapshot.Entries.Single(e => e.Role == ConversationRole.Assistant);
        Assert.Equal(EntryStatus.Completed, assistant.Status);
        Assert.Equal("T1", assistant.Text);
    }

    [Fact]
    public async Task Cancel_after_transport_detach_still_interrupts()
    {
        var output = new CapturingSessionOutput();
        var model = new DetachHoldingLanguageModel();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        var runtime = CreateRuntime(
            output,
            model,
            time,
            new DefaultAgentBrain(new PromptContextBuilder()));
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello");
        await WaitForActiveResponseAsync(runtime);
        var responseId = runtime.ActiveResponseId!.Value;

        await runtime.DetachAsync();
        Assert.Equal(ResponseCancelResult.Cancelled, await runtime.CancelResponseAsync(responseId));
        model.Release.TrySetResult();
        await runtime.WaitUntilIdleAsync();

        Assert.Contains(
            output.Items,
            item => item.ResponseId == responseId
                && item.Payload is ResponseCompletedOutput completed
                && completed.InterruptReason == "userStop");
        await runtime.DisposeAsync();
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (await predicate().ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        Assert.Fail("Condition was not met before timeout.");
    }

    private static async Task WaitForActiveResponseAsync(SessionRuntime runtime)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (runtime.ActiveResponseId is not null)
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("Active response was not established.");
    }

    private static SessionRuntime CreateRuntime(
        ISessionOutput output,
        ILanguageModel model,
        FakeTimeProvider time,
        IAgentBrain brain,
        IMemoryStore? store = null,
        SessionSnapshot? snapshot = null,
        InMemoryConversationTurnExecutionStore? turnExecutions = null,
        ILogger<SessionRuntime>? logger = null)
    {
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 256).Select(index => Guid.Parse($"019944af-0000-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842")]);
        var memory = store ?? new InMemoryMemoryStore();
        var now = time.GetUtcNow();
        var initial = snapshot ?? new SessionSnapshot(
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
            now,
            now,
            ModelSelection: new SessionModelSelection(
                "synthetic-offline/scripted",
                "primary-llm",
                "scripted",
                ModelSelectionSource.SystemDefault,
                null));
        if (snapshot is null)
        {
            memory.SaveAsync(initial, 0).AsTask().GetAwaiter().GetResult();
        }

        turnExecutions ??= new InMemoryConversationTurnExecutionStore();
        return new SessionRuntime(
            initial,
            model,
            brain,
            memory,
            output,
            ids,
            time,
            logger ?? NullLogger<SessionRuntime>.Instance,
            new FakeInterruptionClassifier(),
            turnExecutions: turnExecutions);
    }

    private sealed class TerminalAssistantPersistGate(TaskCompletionSource release) : IMemoryStore
    {
        private readonly InMemoryMemoryStore _inner = new();
        public TaskCompletionSource TerminalSaveStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => release.TrySetResult();

        public async Task WaitForSaveStartedAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            await TerminalSaveStarted.Task.WaitAsync(cts.Token).ConfigureAwait(false);
        }

        public ValueTask<SessionSnapshot?> LoadAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            _inner.LoadAsync(sessionId, cancellationToken);

        public async ValueTask SaveAsync(
            SessionSnapshot snapshot,
            long expectedRevision,
            CancellationToken cancellationToken = default)
        {
            if (IsTerminalAssistantPersist(snapshot))
            {
                TerminalSaveStarted.TrySetResult();
                await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            await _inner.SaveAsync(snapshot, expectedRevision, cancellationToken).ConfigureAwait(false);
        }

        private static bool IsTerminalAssistantPersist(SessionSnapshot snapshot)
        {
            var assistant = snapshot.Entries.LastOrDefault(entry => entry.Role == ConversationRole.Assistant);
            return assistant is { Status: EntryStatus.Completed }
                && !string.IsNullOrWhiteSpace(assistant.Text);
        }

        public ValueTask<IReadOnlyList<ConversationEntry>> ReadHistoryAsync(
            Guid sessionId,
            long afterEntrySequence,
            int limit,
            CancellationToken cancellationToken = default) =>
            _inner.ReadHistoryAsync(sessionId, afterEntrySequence, limit, cancellationToken);

        public ValueTask<ConversationHistoryPage?> ReadHistoryPageAsync(
            Guid sessionId,
            long? afterEntrySequence,
            long? beforeEntrySequence,
            int limit,
            CancellationToken cancellationToken = default) =>
            _inner.ReadHistoryPageAsync(sessionId, afterEntrySequence, beforeEntrySequence, limit, cancellationToken);

        public ValueTask<UserProfile?> LoadProfileAsync(Guid profileId, CancellationToken cancellationToken = default) =>
            _inner.LoadProfileAsync(profileId, cancellationToken);

        public ValueTask SaveProfileAsync(
            UserProfile profile,
            long expectedRevision,
            CancellationToken cancellationToken = default) =>
            _inner.SaveProfileAsync(profile, expectedRevision, cancellationToken);

        public ValueTask RecoverCrashedSessionsAsync(CancellationToken cancellationToken = default) =>
            _inner.RecoverCrashedSessionsAsync(cancellationToken);
    }
}

file sealed class DetachHoldingLanguageModel : ILanguageModel
{
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ModelCapabilities Capabilities { get; } = new(true, true);

    public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
        ModelRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new ModelTextDelta("T1");
        await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        yield return new ModelCompleted(ModelStopReason.Completed);
    }
}
