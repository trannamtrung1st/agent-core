using AgentCore.Application.Agents;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class PersistenceReceiptTests
{
    [Fact]
    public async Task AttachAsync_honors_a_cancelled_token()
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new HoldingSaveStore(hold.Task);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 64).Select(index => Guid.Parse($"019944af-0000-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842")]);
        var now = time.GetUtcNow();
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
            now,
            now);
        await store.Inner.SaveAsync(snapshot, 0);
        await using var runtime = new SessionRuntime(
            snapshot,
            new ScriptedLanguageModel(),
            new DefaultAgentBrain(new PromptContextBuilder()),
            store,
            new CapturingSessionOutput(),
            ids,
            time,
            NullLogger<SessionRuntime>.Instance);
        using var cancelled = new CancellationTokenSource();
        var attaching = runtime.AttachAsync(cancelled.Token);
        await store.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancelled.Cancel();
        Assert.False(await attaching.WaitAsync(TimeSpan.FromSeconds(2)));
        hold.TrySetResult();
    }

    [Fact]
    public async Task RequestEndAsync_honors_a_cancelled_token()
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new HoldingSaveStore(hold.Task) { Hold = false };
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 64).Select(index => Guid.Parse($"019944af-0000-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842")]);
        var now = time.GetUtcNow();
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
            now,
            now);
        await store.Inner.SaveAsync(snapshot, 0);
        await using var runtime = new SessionRuntime(
            snapshot,
            new ScriptedLanguageModel(),
            new DefaultAgentBrain(new PromptContextBuilder()),
            store,
            new CapturingSessionOutput(),
            ids,
            time,
            NullLogger<SessionRuntime>.Instance);
        Assert.True(await runtime.AttachAsync());
        store.Arm();
        using var cancelled = new CancellationTokenSource();
        var ending = runtime.RequestEndAsync(cancelled.Token);
        await store.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancelled.Cancel();
        Assert.False(await ending.WaitAsync(TimeSpan.FromSeconds(2)));
        hold.TrySetResult();
    }

    [Fact]
    public async Task User_turn_does_not_start_generation_until_save_acknowledges()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new GatedMemoryStore(gate);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        var brain = new RecordingAgentBrain(new DefaultAgentBrain(new PromptContextBuilder()));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 64).Select(index => Guid.Parse($"019944af-0000-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842")]);
        var now = time.GetUtcNow();
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
            now,
            now);
        await store.SaveAsync(snapshot, 0);
        await using var runtime = new SessionRuntime(
            snapshot,
            new ScriptedLanguageModel(),
            brain,
            store,
            new CapturingSessionOutput(),
            ids,
            time,
            NullLogger<SessionRuntime>.Instance);
        await runtime.AttachAsync();
        await runtime.WaitUntilMailboxDrainedAsync();
        var pending = runtime.SubmitUserTextAsync("Hello");
        await store.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, brain.Calls);
        gate.TrySetResult();
        await pending;
        await runtime.WaitUntilIdleAsync();
        Assert.True(brain.Calls >= 1);
    }

    [Fact]
    public async Task Text_completion_publishes_only_after_terminal_save_acknowledges()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new CompletionGatedStore(gate);
        var output = new CapturingSessionOutput();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new ScriptedLanguageModel(["Hello"], releaseAfterFirstChunk: release);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 64).Select(index => Guid.Parse($"019944af-0000-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842")]);
        var now = time.GetUtcNow();
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
            now,
            now);
        await store.SaveAsync(snapshot, 0);
        await using var runtime = new SessionRuntime(
            snapshot,
            model,
            new DefaultAgentBrain(new PromptContextBuilder()),
            store,
            output,
            ids,
            time,
            NullLogger<SessionRuntime>.Instance);
        await runtime.AttachAsync();
        await runtime.WaitUntilMailboxDrainedAsync();
        await runtime.SubmitUserTextAsync("Hello");
        await output.WaitForAsync(item => item.Payload is ResponseStartedOutput);
        release.TrySetResult();
        await output.WaitForAsync(item => item.Payload is TextDeltaOutput);
        await store.CompletionSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.DoesNotContain(output.Items, item => item.Payload is TextCompletedOutput);
        Assert.DoesNotContain(output.Items, item => item.Payload is ResponseCompletedOutput);
        gate.TrySetResult();
        await runtime.WaitUntilIdleAsync();
        Assert.Contains(output.Items, item => item.Payload is TextCompletedOutput);
        Assert.Contains(output.Items, item => item.Payload is ResponseCompletedOutput);
    }

    [Fact]
    public async Task Failed_terminal_end_save_does_not_ack_ended_status()
    {
        await using var harness = await SqliteTestHarness.CreateMigratedAsync();
        var store = new FailingEndStore(harness.Store);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 64).Select(index => Guid.Parse($"019944af-0000-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842")]);
        var now = time.GetUtcNow();
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
            now,
            now);
        await harness.Store.SaveAsync(snapshot, 0);
        await using var runtime = new SessionRuntime(
            snapshot,
            new ScriptedLanguageModel(),
            new DefaultAgentBrain(new PromptContextBuilder()),
            store,
            new CapturingSessionOutput(),
            ids,
            time,
            NullLogger<SessionRuntime>.Instance);
        await runtime.AttachAsync();
        await runtime.WaitUntilMailboxDrainedAsync();
        var ending = runtime.RequestEndAsync();
        await PumpUntilAsync(time, ending);
        await runtime.WaitUntilMailboxDrainedAsync();
        var loaded = await harness.Store.LoadAsync(snapshot.SessionId);
        Assert.NotEqual(SessionStatus.Ended, loaded!.Status);
        Assert.True(store.Failed);
    }

    [Fact]
    public async Task Failed_later_pause_save_cannot_revert_ended_status()
    {
        var inner = new InMemoryMemoryStore();
        var store = new PauseAfterEndStore(inner);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 64).Select(index => Guid.Parse($"019944af-0000-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842")]);
        var now = time.GetUtcNow();
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
            now,
            now);
        await inner.SaveAsync(snapshot, 0);
        await using var runtime = new SessionRuntime(
            snapshot,
            new ScriptedLanguageModel(),
            new DefaultAgentBrain(new PromptContextBuilder()),
            store,
            new CapturingSessionOutput(),
            ids,
            time,
            NullLogger<SessionRuntime>.Instance);
        await runtime.AttachAsync();
        await runtime.WaitUntilMailboxDrainedAsync();
        Assert.True(await runtime.RequestEndAsync());
        await runtime.DetachAsync();
        await runtime.WaitUntilMailboxDrainedAsync();
        var loaded = await inner.LoadAsync(snapshot.SessionId);
        Assert.Equal(SessionStatus.Ended, loaded!.Status);
        Assert.Equal(0, store.PauseAfterEnd);
    }

    [Fact]
    public async Task Interrupt_and_end_remain_responsive_during_persist_backoff()
    {
        var store = new FailingUserTurnStore();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 64).Select(index => Guid.Parse($"019944af-0000-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842")]);
        var now = time.GetUtcNow();
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
            now,
            now);
        await store.Inner.SaveAsync(snapshot, 0);
        await using var runtime = new SessionRuntime(
            snapshot,
            new ScriptedLanguageModel(),
            new DefaultAgentBrain(new PromptContextBuilder()),
            store,
            new CapturingSessionOutput(),
            ids,
            time,
            NullLogger<SessionRuntime>.Instance);
        await runtime.AttachAsync();
        await runtime.WaitUntilMailboxDrainedAsync();
        Assert.True(await runtime.SubmitUserTextAsync("Hello"));
        await store.Failed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var ending = runtime.RequestEndAsync();
        Assert.True(await ending.WaitAsync(TimeSpan.FromSeconds(2)));
        await runtime.WaitUntilMailboxDrainedAsync();
        var loaded = await store.Inner.LoadAsync(snapshot.SessionId);
        Assert.Equal(SessionStatus.Ended, loaded!.Status);
    }

    [Fact]
    public async Task Continued_persist_failure_pauses_without_starting_generation()
    {
        var store = new FailingUserTurnStore();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        var brain = new RecordingAgentBrain(new DefaultAgentBrain(new PromptContextBuilder()));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 64).Select(index => Guid.Parse($"019944af-0000-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842")]);
        var now = time.GetUtcNow();
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
            now,
            now);
        await store.Inner.SaveAsync(snapshot, 0);
        var output = new CapturingSessionOutput();
        await using var runtime = new SessionRuntime(
            snapshot,
            new ScriptedLanguageModel(),
            brain,
            store,
            output,
            ids,
            time,
            NullLogger<SessionRuntime>.Instance);
        await runtime.AttachAsync();
        await runtime.WaitUntilMailboxDrainedAsync();
        Assert.True(await runtime.SubmitUserTextAsync("Hello"));
        await store.Failed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        for (var attempt = 0; attempt < 12 && runtime.Snapshot.Status != SessionStatus.Paused; attempt++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(10);
        }

        await runtime.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SessionStatus.Paused, runtime.Snapshot.Status);
        Assert.Equal(0, brain.Calls);
        Assert.DoesNotContain(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.User);
        Assert.Contains(output.Items, item => item.Payload is ErrorOutput error && error.Code == "SessionPersistenceUnavailable");
        var loaded = await store.Inner.LoadAsync(snapshot.SessionId);
        Assert.DoesNotContain(loaded!.Entries, entry => entry.Role == ConversationRole.User);
    }

    [Fact]
    public async Task Pause_during_user_persist_keeps_committed_user_text()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new GatedMemoryStore(gate);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 64).Select(index => Guid.Parse($"019944af-0000-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842")]);
        var now = time.GetUtcNow();
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
            now,
            now);
        await store.SaveAsync(snapshot, 0);
        await using var runtime = new SessionRuntime(
            snapshot,
            new ScriptedLanguageModel(),
            new DefaultAgentBrain(new PromptContextBuilder()),
            store,
            new CapturingSessionOutput(),
            ids,
            time,
            NullLogger<SessionRuntime>.Instance);
        await runtime.AttachAsync();
        await runtime.WaitUntilMailboxDrainedAsync();
        Assert.True(await runtime.SubmitUserTextAsync("Hello"));
        await store.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await runtime.DetachAsync();
        gate.TrySetResult();
        await runtime.WaitUntilIdleAsync();
        var loaded = await store.LoadAsync(snapshot.SessionId);
        Assert.Equal(SessionStatus.Paused, loaded!.Status);
        Assert.Contains(loaded.Entries, entry => entry.Role == ConversationRole.User && entry.Text == "Hello");
    }

    [Fact]
    public async Task End_while_voice_response_live_emits_stop_and_interrupted()
    {
        var output = new CapturingSessionOutput();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 64).Select(index => Guid.Parse($"019944af-0000-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842")]);
        var now = time.GetUtcNow();
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
            now,
            now);
        var store = new InMemoryMemoryStore();
        await store.SaveAsync(snapshot, 0);
        await using var runtime = new SessionRuntime(
            snapshot,
            new ScriptedLanguageModel(["There are three points. "]),
            new DefaultAgentBrain(new PromptContextBuilder()),
            store,
            output,
            ids,
            time,
            NullLogger<SessionRuntime>.Instance,
            recognizer: new SyntheticSpeechRecognizer(),
            synthesizer: new SyntheticSpeechSynthesizer());
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        await runtime.SubmitUserTextAsync("Hello");
        await output.WaitForAsync(item => item.Payload is AudioFrameOutput);
        Assert.NotNull(runtime.ActiveResponseId);
        Assert.True(await runtime.RequestEndAsync());
        await runtime.WaitUntilIdleAsync();
        Assert.Contains(output.Items, item => item.Payload is PlaybackStopOutput);
        Assert.Contains(
            output.Items,
            item => item.Payload is ResponseCompletedOutput completed && completed.InterruptReason == "ended");
    }

    [Fact]
    public async Task Detach_while_voice_response_live_emits_stop_and_interrupted()
    {
        var output = new CapturingSessionOutput();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 64).Select(index => Guid.Parse($"019944af-0000-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842")]);
        var now = time.GetUtcNow();
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
            now,
            now);
        var store = new InMemoryMemoryStore();
        await store.SaveAsync(snapshot, 0);
        await using var runtime = new SessionRuntime(
            snapshot,
            new ScriptedLanguageModel(["There are three points. "]),
            new DefaultAgentBrain(new PromptContextBuilder()),
            store,
            output,
            ids,
            time,
            NullLogger<SessionRuntime>.Instance,
            recognizer: new SyntheticSpeechRecognizer(),
            synthesizer: new SyntheticSpeechSynthesizer());
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        await runtime.SubmitUserTextAsync("Hello");
        await output.WaitForAsync(item => item.Payload is AudioFrameOutput);
        Assert.NotNull(runtime.ActiveResponseId);
        await runtime.DetachAsync();
        await runtime.WaitUntilIdleAsync();
        Assert.Contains(output.Items, item => item.Payload is PlaybackStopOutput);
        Assert.Contains(
            output.Items,
            item => item.Payload is ResponseCompletedOutput completed && completed.InterruptReason == "disconnected");
    }

    [Fact]
    public async Task SetMode_during_pause_persist_keeps_later_mailbox_mode()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new PauseGatedStore(gate);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 64).Select(index => Guid.Parse($"019944af-0000-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842")]);
        var now = time.GetUtcNow();
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
            now,
            now);
        await store.SaveAsync(snapshot, 0);
        await using var runtime = new SessionRuntime(
            snapshot,
            new ScriptedLanguageModel(),
            new DefaultAgentBrain(new PromptContextBuilder()),
            store,
            new CapturingSessionOutput(),
            ids,
            time,
            NullLogger<SessionRuntime>.Instance);
        await runtime.AttachAsync();
        await runtime.WaitUntilMailboxDrainedAsync();
        var detaching = runtime.DetachAsync();
        await store.PauseSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await runtime.SetModeAsync(SessionMode.Voice);
        await runtime.WaitUntilMailboxDrainedAsync();
        Assert.Equal(SessionMode.Voice, runtime.Snapshot.PendingMode);
        gate.TrySetResult();
        await detaching;
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(SessionMode.Voice, runtime.Snapshot.PendingMode);
        Assert.Equal(SessionStatus.Paused, runtime.Snapshot.Status);
    }

    [Fact]
    public async Task Queued_checkpoints_coalesce_to_the_newest()
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new CountingHoldStore(hold);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 64).Select(index => Guid.Parse($"019944af-0000-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842")]);
        var now = time.GetUtcNow();
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
            now,
            now);
        await store.SaveAsync(snapshot, 0);
        var output = new CapturingSessionOutput();
        await using var runtime = new SessionRuntime(
            snapshot,
            new ScriptedLanguageModel(["There are three points."]),
            new DefaultAgentBrain(new PromptContextBuilder()),
            store,
            output,
            ids,
            time,
            NullLogger<SessionRuntime>.Instance,
            recognizer: new SyntheticSpeechRecognizer(),
            synthesizer: new SyntheticSpeechSynthesizer());
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        await runtime.SubmitUserTextAsync("Hello");
        var audio = await output.WaitForAsync(item => item.Payload is AudioFrameOutput);
        await runtime.WaitUntilMailboxDrainedAsync();
        store.Hold = true;
        var before = store.Saves;
        var responseId = audio.ResponseId!.Value;
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await runtime.SubmitPlaybackAsync(responseId, "started", 0, 0));
        for (var index = 1; index <= 8; index++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            var consumed = Math.Min(index * 480, Math.Max(480, runtime.SentSamples));
            await runtime.SubmitPlaybackAsync(responseId, "progress", consumed, 0);
        }

        await store.Held.Task.WaitAsync(TimeSpan.FromSeconds(2));
        hold.TrySetResult();
        store.Hold = false;
        await runtime.WaitUntilMailboxDrainedAsync();
        for (var attempt = 0; attempt < 40 && Volatile.Read(ref store.InFlight) > 0; attempt++)
        {
            await Task.Delay(10);
        }

        Assert.InRange(store.Saves - before, 1, 3);
    }

    private static async Task PumpUntilAsync(FakeTimeProvider time, Task task)
    {
        for (var attempt = 0; attempt < 50 && !task.IsCompleted; attempt++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(5);
        }

        await task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class CountingHoldStore(TaskCompletionSource hold) : IMemoryStore
    {
        private readonly InMemoryMemoryStore _inner = new();
        public int Saves;
        public int InFlight;
        public bool Hold;
        public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<SessionSnapshot?> LoadAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            _inner.LoadAsync(sessionId, cancellationToken);

        public async ValueTask SaveAsync(
            SessionSnapshot snapshot,
            long expectedRevision,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Saves);
            Interlocked.Increment(ref InFlight);
            try
            {
                if (Hold)
                {
                    Held.TrySetResult();
                    await hold.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                await _inner.SaveAsync(snapshot, expectedRevision, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref InFlight);
            }
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

        public ValueTask<UserProfile?> LoadProfileAsync(
            Guid profileId,
            CancellationToken cancellationToken = default) =>
            _inner.LoadProfileAsync(profileId, cancellationToken);

        public ValueTask SaveProfileAsync(
            UserProfile profile,
            long expectedRevision,
            CancellationToken cancellationToken = default) =>
            _inner.SaveProfileAsync(profile, expectedRevision, cancellationToken);

        public ValueTask RecoverCrashedSessionsAsync(CancellationToken cancellationToken = default) =>
            _inner.RecoverCrashedSessionsAsync(cancellationToken);
    }

    private sealed class HoldingSaveStore(Task hold) : IMemoryStore
    {
        public InMemoryMemoryStore Inner { get; } = new();
        public bool Hold { get; set; } = true;
        public TaskCompletionSource SaveStarted { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Arm()
        {
            Hold = true;
            SaveStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public ValueTask<SessionSnapshot?> LoadAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            Inner.LoadAsync(sessionId, cancellationToken);

        public async ValueTask SaveAsync(
            SessionSnapshot snapshot,
            long expectedRevision,
            CancellationToken cancellationToken = default)
        {
            if (Hold)
            {
                SaveStarted.TrySetResult();
                await hold.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            await Inner.SaveAsync(snapshot, expectedRevision, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask<IReadOnlyList<ConversationEntry>> ReadHistoryAsync(
            Guid sessionId,
            long afterEntrySequence,
            int limit,
            CancellationToken cancellationToken = default) =>
            Inner.ReadHistoryAsync(sessionId, afterEntrySequence, limit, cancellationToken);

        public ValueTask<ConversationHistoryPage?> ReadHistoryPageAsync(
            Guid sessionId,
            long? afterEntrySequence,
            long? beforeEntrySequence,
            int limit,
            CancellationToken cancellationToken = default) =>
            Inner.ReadHistoryPageAsync(sessionId, afterEntrySequence, beforeEntrySequence, limit, cancellationToken);

        public ValueTask<UserProfile?> LoadProfileAsync(
            Guid profileId,
            CancellationToken cancellationToken = default) =>
            Inner.LoadProfileAsync(profileId, cancellationToken);

        public ValueTask SaveProfileAsync(
            UserProfile profile,
            long expectedRevision,
            CancellationToken cancellationToken = default) =>
            Inner.SaveProfileAsync(profile, expectedRevision, cancellationToken);

        public ValueTask RecoverCrashedSessionsAsync(CancellationToken cancellationToken = default) =>
            Inner.RecoverCrashedSessionsAsync(cancellationToken);
    }

    private sealed class CompletionGatedStore(TaskCompletionSource gate) : IMemoryStore
    {
        private readonly InMemoryMemoryStore _inner = new();
        public TaskCompletionSource CompletionSaveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<SessionSnapshot?> LoadAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            _inner.LoadAsync(sessionId, cancellationToken);

        public async ValueTask SaveAsync(
            SessionSnapshot snapshot,
            long expectedRevision,
            CancellationToken cancellationToken = default)
        {
            if (snapshot.Entries.Any(entry =>
                    entry.Role == ConversationRole.Assistant
                    && entry.Status is EntryStatus.Completed or EntryStatus.Interrupted))
            {
                CompletionSaveStarted.TrySetResult();
                await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            await _inner.SaveAsync(snapshot, expectedRevision, cancellationToken).ConfigureAwait(false);
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

        public ValueTask<UserProfile?> LoadProfileAsync(
            Guid profileId,
            CancellationToken cancellationToken = default) =>
            _inner.LoadProfileAsync(profileId, cancellationToken);

        public ValueTask SaveProfileAsync(
            UserProfile profile,
            long expectedRevision,
            CancellationToken cancellationToken = default) =>
            _inner.SaveProfileAsync(profile, expectedRevision, cancellationToken);

        public ValueTask RecoverCrashedSessionsAsync(CancellationToken cancellationToken = default) =>
            _inner.RecoverCrashedSessionsAsync(cancellationToken);
    }

    private sealed class PauseGatedStore(TaskCompletionSource gate) : IMemoryStore
    {
        private readonly InMemoryMemoryStore _inner = new();
        public TaskCompletionSource PauseSaveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<SessionSnapshot?> LoadAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            _inner.LoadAsync(sessionId, cancellationToken);

        public async ValueTask SaveAsync(
            SessionSnapshot snapshot,
            long expectedRevision,
            CancellationToken cancellationToken = default)
        {
            if (snapshot.Status == SessionStatus.Paused)
            {
                PauseSaveStarted.TrySetResult();
                await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            await _inner.SaveAsync(snapshot, expectedRevision, cancellationToken).ConfigureAwait(false);
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

        public ValueTask<UserProfile?> LoadProfileAsync(
            Guid profileId,
            CancellationToken cancellationToken = default) =>
            _inner.LoadProfileAsync(profileId, cancellationToken);

        public ValueTask SaveProfileAsync(
            UserProfile profile,
            long expectedRevision,
            CancellationToken cancellationToken = default) =>
            _inner.SaveProfileAsync(profile, expectedRevision, cancellationToken);

        public ValueTask RecoverCrashedSessionsAsync(CancellationToken cancellationToken = default) =>
            _inner.RecoverCrashedSessionsAsync(cancellationToken);
    }

    private sealed class GatedMemoryStore(TaskCompletionSource gate) : IMemoryStore
    {
        private readonly InMemoryMemoryStore _inner = new();
        public TaskCompletionSource SaveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<SessionSnapshot?> LoadAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            _inner.LoadAsync(sessionId, cancellationToken);

        public async ValueTask SaveAsync(
            SessionSnapshot snapshot,
            long expectedRevision,
            CancellationToken cancellationToken = default)
        {
            if (snapshot.Entries.Count > 0)
            {
                SaveStarted.TrySetResult();
                await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            await _inner.SaveAsync(snapshot, expectedRevision, cancellationToken).ConfigureAwait(false);
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

        public ValueTask<UserProfile?> LoadProfileAsync(
            Guid profileId,
            CancellationToken cancellationToken = default) =>
            _inner.LoadProfileAsync(profileId, cancellationToken);

        public ValueTask SaveProfileAsync(
            UserProfile profile,
            long expectedRevision,
            CancellationToken cancellationToken = default) =>
            _inner.SaveProfileAsync(profile, expectedRevision, cancellationToken);

        public ValueTask RecoverCrashedSessionsAsync(CancellationToken cancellationToken = default) =>
            _inner.RecoverCrashedSessionsAsync(cancellationToken);
    }

    private sealed class FailingEndStore(IMemoryStore inner) : IMemoryStore
    {
        public bool Failed { get; private set; }

        public ValueTask<SessionSnapshot?> LoadAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            inner.LoadAsync(sessionId, cancellationToken);

        public ValueTask SaveAsync(SessionSnapshot snapshot, long expectedRevision, CancellationToken cancellationToken = default)
        {
            if (snapshot.Status == SessionStatus.Ended)
            {
                Failed = true;
                throw AgentCoreErrors.Persistence("forced end save failure");
            }

            return inner.SaveAsync(snapshot, expectedRevision, cancellationToken);
        }

        public ValueTask<IReadOnlyList<ConversationEntry>> ReadHistoryAsync(
            Guid sessionId,
            long afterEntrySequence,
            int limit,
            CancellationToken cancellationToken = default) =>
            inner.ReadHistoryAsync(sessionId, afterEntrySequence, limit, cancellationToken);

        public ValueTask<ConversationHistoryPage?> ReadHistoryPageAsync(
            Guid sessionId,
            long? afterEntrySequence,
            long? beforeEntrySequence,
            int limit,
            CancellationToken cancellationToken = default) =>
            inner.ReadHistoryPageAsync(sessionId, afterEntrySequence, beforeEntrySequence, limit, cancellationToken);

        public ValueTask<UserProfile?> LoadProfileAsync(Guid profileId, CancellationToken cancellationToken = default) =>
            inner.LoadProfileAsync(profileId, cancellationToken);

        public ValueTask SaveProfileAsync(UserProfile profile, long expectedRevision, CancellationToken cancellationToken = default) =>
            inner.SaveProfileAsync(profile, expectedRevision, cancellationToken);

        public ValueTask RecoverCrashedSessionsAsync(CancellationToken cancellationToken = default) =>
            inner.RecoverCrashedSessionsAsync(cancellationToken);
    }

    private sealed class PauseAfterEndStore(IMemoryStore inner) : IMemoryStore
    {
        private bool _ended;

        public int PauseAfterEnd { get; private set; }

        public ValueTask<SessionSnapshot?> LoadAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            inner.LoadAsync(sessionId, cancellationToken);

        public ValueTask SaveAsync(SessionSnapshot snapshot, long expectedRevision, CancellationToken cancellationToken = default)
        {
            if (snapshot.Status == SessionStatus.Ended)
            {
                _ended = true;
            }

            if (snapshot.Status == SessionStatus.Paused && _ended)
            {
                PauseAfterEnd++;
                throw AgentCoreErrors.Persistence("forced pause after end");
            }

            return inner.SaveAsync(snapshot, expectedRevision, cancellationToken);
        }

        public ValueTask<IReadOnlyList<ConversationEntry>> ReadHistoryAsync(
            Guid sessionId,
            long afterEntrySequence,
            int limit,
            CancellationToken cancellationToken = default) =>
            inner.ReadHistoryAsync(sessionId, afterEntrySequence, limit, cancellationToken);

        public ValueTask<ConversationHistoryPage?> ReadHistoryPageAsync(
            Guid sessionId,
            long? afterEntrySequence,
            long? beforeEntrySequence,
            int limit,
            CancellationToken cancellationToken = default) =>
            inner.ReadHistoryPageAsync(sessionId, afterEntrySequence, beforeEntrySequence, limit, cancellationToken);

        public ValueTask<UserProfile?> LoadProfileAsync(Guid profileId, CancellationToken cancellationToken = default) =>
            inner.LoadProfileAsync(profileId, cancellationToken);

        public ValueTask SaveProfileAsync(
            UserProfile profile,
            long expectedRevision,
            CancellationToken cancellationToken = default) =>
            inner.SaveProfileAsync(profile, expectedRevision, cancellationToken);

        public ValueTask RecoverCrashedSessionsAsync(CancellationToken cancellationToken = default) =>
            inner.RecoverCrashedSessionsAsync(cancellationToken);
    }

    private sealed class FailingUserTurnStore : IMemoryStore
    {
        public InMemoryMemoryStore Inner { get; } = new();
        public TaskCompletionSource Failed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<SessionSnapshot?> LoadAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            Inner.LoadAsync(sessionId, cancellationToken);

        public ValueTask SaveAsync(SessionSnapshot snapshot, long expectedRevision, CancellationToken cancellationToken = default)
        {
            if (snapshot.Entries.Count > 0 && snapshot.Status is not SessionStatus.Ended and not SessionStatus.Ending)
            {
                Failed.TrySetResult();
                throw AgentCoreErrors.Persistence("forced user persist failure");
            }

            return Inner.SaveAsync(snapshot, expectedRevision, cancellationToken);
        }

        public ValueTask<IReadOnlyList<ConversationEntry>> ReadHistoryAsync(
            Guid sessionId,
            long afterEntrySequence,
            int limit,
            CancellationToken cancellationToken = default) =>
            Inner.ReadHistoryAsync(sessionId, afterEntrySequence, limit, cancellationToken);

        public ValueTask<ConversationHistoryPage?> ReadHistoryPageAsync(
            Guid sessionId,
            long? afterEntrySequence,
            long? beforeEntrySequence,
            int limit,
            CancellationToken cancellationToken = default) =>
            Inner.ReadHistoryPageAsync(sessionId, afterEntrySequence, beforeEntrySequence, limit, cancellationToken);

        public ValueTask<UserProfile?> LoadProfileAsync(Guid profileId, CancellationToken cancellationToken = default) =>
            Inner.LoadProfileAsync(profileId, cancellationToken);

        public ValueTask SaveProfileAsync(UserProfile profile, long expectedRevision, CancellationToken cancellationToken = default) =>
            Inner.SaveProfileAsync(profile, expectedRevision, cancellationToken);

        public ValueTask RecoverCrashedSessionsAsync(CancellationToken cancellationToken = default) =>
            Inner.RecoverCrashedSessionsAsync(cancellationToken);
    }
}
