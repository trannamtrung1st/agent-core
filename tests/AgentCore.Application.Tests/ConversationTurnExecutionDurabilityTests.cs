using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Events;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class ConversationTurnExecutionDurabilityTests
{
    [Fact]
    public async Task User_turn_creates_durable_execution_and_completes_with_assistant()
    {
        var output = new CapturingSessionOutput();
        var store = new InMemoryConversationTurnExecutionStore();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero));
        await using var runtime = CreateRuntime(output, new ScriptedLanguageModel(), time, store);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("hi");
        await runtime.WaitUntilIdleAsync();

        var open = await store.ListOpenForSessionAsync(runtime.SessionId);
        Assert.Empty(open);

        var assistant = runtime.Snapshot.Entries.Single(e => e.Role == ConversationRole.Assistant);
        Assert.Equal(EntryStatus.Completed, assistant.Status);
        Assert.False(string.IsNullOrWhiteSpace(assistant.Text));
    }

    [Fact]
    public async Task Reload_after_runtime_disposal_shows_persisted_assistant()
    {
        var memory = new InMemoryMemoryStore();
        var turns = new InMemoryConversationTurnExecutionStore();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero));
        Guid sessionId;
        await using (var runtime = CreateRuntime(new CapturingSessionOutput(), new ScriptedLanguageModel(), time, turns, memory))
        {
            sessionId = runtime.SessionId;
            await runtime.AttachAsync();
            await runtime.SubmitUserTextAsync("hi");
            await runtime.WaitUntilIdleAsync();
        }

        var durable = (await memory.LoadAsync(sessionId))!;
        await using var restored = CreateRuntime(
            new CapturingSessionOutput(),
            new ScriptedLanguageModel(),
            time,
            turns,
            memory,
            durable);
        var assistant = restored.Snapshot.Entries.Single(entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal(EntryStatus.Completed, assistant.Status);
        Assert.False(string.IsNullOrWhiteSpace(assistant.Text));
        Assert.Empty(await turns.ListOpenForSessionAsync(sessionId));
        Assert.DoesNotContain(
            restored.Snapshot.Entries,
            entry => entry.Role == ConversationRole.Assistant && string.IsNullOrWhiteSpace(entry.Text) && entry.Status != EntryStatus.Streaming);
    }

    [Fact]
    public async Task Repeated_source_event_does_not_duplicate_turn_or_execution()
    {
        var turns = new InMemoryConversationTurnExecutionStore();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero));
        await using var runtime = CreateRuntime(new CapturingSessionOutput(), new ScriptedLanguageModel(), time, turns);
        await runtime.AttachAsync();
        var eventId = Guid.Parse("019944af-0000-7000-8000-000000000099");
        Assert.True(await runtime.SubmitPersistedUserTextAsync("hi", eventId));
        await runtime.WaitUntilIdleAsync();
        Assert.True(await runtime.SubmitPersistedUserTextAsync("hi", eventId));
        await runtime.WaitUntilIdleAsync();

        Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.User);
        Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant);
        Assert.Empty(await turns.ListOpenForSessionAsync(runtime.SessionId));
    }

    [Fact]
    public async Task Queued_turn_survives_detach_and_keeps_order()
    {
        var turns = new InMemoryConversationTurnExecutionStore();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero));
        var model = new HoldingLanguageModel();
        await using var runtime = CreateRuntime(new CapturingSessionOutput(), model, time, turns);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("first");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (runtime.ActiveResponseId is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        var queuedEventId = Guid.Parse("019944af-0000-7000-8000-000000000098");
        Assert.True(await runtime.SubmitPersistedUserTextAsync(
            "second",
            queuedEventId,
            behavior: UserTextBehavior.Queue));
        var thirdEventId = Guid.Parse("019944af-0000-7000-8000-000000000097");
        Assert.True(await runtime.SubmitPersistedUserTextAsync(
            "third",
            thirdEventId,
            behavior: UserTextBehavior.Queue));
        var accepted = await turns.ListOpenForSessionAsync(runtime.SessionId);
        Assert.Equal(3, accepted.Count);
        Assert.Contains(accepted, execution => execution.SourceEventId == queuedEventId);
        Assert.Contains(accepted, execution => execution.SourceEventId == thirdEventId);
        Assert.Single(
            accepted
                .Where(execution => execution.Status == ConversationTurnExecutionStatus.Queued)
                .Select(execution => execution.ResponseId)
                .Distinct());
        await runtime.DetachAsync();
        model.Release.TrySetResult();
        await runtime.WaitUntilIdleAsync();

        var assistants = runtime.Snapshot.Entries.Where(entry => entry.Role == ConversationRole.Assistant).ToArray();
        Assert.Equal(2, assistants.Length);
        Assert.All(assistants, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Text)));
        Assert.Equal(
            ["first", "second", "third"],
            runtime.Snapshot.Entries.Where(entry => entry.Role == ConversationRole.User).Select(entry => entry.Text).ToArray());
        Assert.Empty(await turns.ListOpenForSessionAsync(runtime.SessionId));
    }

    [Fact]
    public async Task Transport_detach_then_reattach_ready_restores_open_execution_projection()
    {
        var output = new CapturingSessionOutput();
        var turns = new InMemoryConversationTurnExecutionStore();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero));
        var model = new HoldingLanguageModel();
        await using var runtime = CreateRuntime(output, model, time, turns);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("[test:durable-stream] resume");
        await output.WaitForAsync(item => item.Payload is TextDeltaOutput);
        var open = await turns.ListOpenForSessionAsync(runtime.SessionId);
        var executionId = Assert.Single(open).ExecutionId;
        var responseId = runtime.ActiveResponseId;
        Assert.NotNull(responseId);

        await runtime.TransportDetachAsync();
        Assert.True(await runtime.AttachAsync());
        await runtime.WaitUntilMailboxDrainedAsync();

        var ready = Assert.IsType<ReadyOutput>(
            output.Items.Last(item => item.Payload is ReadyOutput).Payload);
        Assert.Equal(executionId, ready.Ready.ConversationExecutionId);
        Assert.Equal(responseId, ready.Ready.ActiveResponseId);
        var streaming = Assert.Single(
            ready.Ready.History,
            entry => entry.ResponseId == responseId);
        Assert.Equal(EntryStatus.Streaming, streaming.Status);
        Assert.False(string.IsNullOrEmpty(streaming.Text));

        model.Release.TrySetResult();
        await runtime.WaitUntilIdleAsync();
    }

    [Fact]
    public async Task Stop_cancels_durable_execution_after_interrupted_assistant_persists()
    {
        var turns = new InMemoryConversationTurnExecutionStore();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero));
        var model = new HoldingLanguageModel();
        await using var runtime = CreateRuntime(new CapturingSessionOutput(), model, time, turns);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("hi");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (runtime.ActiveResponseId is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.NotNull(runtime.ActiveResponseId);
        Assert.Equal(ResponseCancelResult.Cancelled, await runtime.CancelResponseAsync(runtime.ActiveResponseId.Value));
        model.Release.TrySetResult();
        await runtime.WaitUntilIdleAsync();

        var assistant = runtime.Snapshot.Entries.Single(entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal(EntryStatus.Interrupted, assistant.Status);
        Assert.Empty(await turns.ListOpenForSessionAsync(runtime.SessionId));
    }

    private static SessionRuntime CreateRuntime(
        ISessionOutput output,
        ILanguageModel model,
        FakeTimeProvider time,
        InMemoryConversationTurnExecutionStore turnExecutions,
        InMemoryMemoryStore? memory = null,
        SessionSnapshot? snapshot = null)
    {
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 256).Select(index => Guid.Parse($"019944af-0000-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842")]);
        memory ??= new InMemoryMemoryStore();
        var now = time.GetUtcNow();
        snapshot ??= new SessionSnapshot(
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
                null), AgentInstanceId: Guid.NewGuid());
        if (snapshot.Revision == 1 && snapshot.Entries.Count == 0)
        {
            memory.SaveAsync(snapshot, 0).AsTask().GetAwaiter().GetResult();
        }

        return new SessionRuntime(
            snapshot,
            model,
            new DefaultAgentBrain(new PromptContextBuilder()),
            memory,
            output,
            ids,
            time,
            NullLogger<SessionRuntime>.Instance,
            new FakeInterruptionClassifier(),
            turnExecutions: turnExecutions);
    }

    private sealed class HoldingLanguageModel : ILanguageModel
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
}
