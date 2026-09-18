using AgentCore.Application.Observability;
using AgentCore.Application.Audio;
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

public sealed class SpeechIngressTests
{
    [Fact]
    public async Task Synthetic_voice_commits_one_user_turn_per_utterance()
    {
        var output = new CapturingSessionOutput();
        RuntimeTelemetry.Reset();
        RuntimeTelemetry.Configure(64, contentLogging: false);
        var recognizer = new SyntheticSpeechRecognizer(["Hello there"]);
        await using var runtime = Create(output, recognizer);
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);

        var utterance = Guid.Parse("019944af-0000-7000-8000-0000000000f1");
        Assert.True(runtime.TryAdmitAudio(Frame(1, 0)));
        Assert.True(runtime.TryAdmitBoundary(utterance, SpeechBoundary.Started, 0.9));
        Assert.True(runtime.TryAdmitBoundary(utterance, SpeechBoundary.Ended, 0.2));
        await output.WaitForAsync(item => item.Payload is TranscriptFinalOutput);
        await runtime.WaitUntilIdleAsync();

        Assert.Equal(1, runtime.Snapshot.Entries.Count(entry => entry.Role == ConversationRole.User && entry.Text == "Hello there"));
        Assert.Contains(output.Items, item => item.Payload is TranscriptPartialOutput);
        Assert.Single(output.Items.Select(item => item.Payload).OfType<TranscriptFinalOutput>());
        Assert.Contains(RuntimeTelemetry.SnapshotTimeline(), item => item.Stage == SpeechTelemetry.PartialCountInstrument);
        Assert.Contains(RuntimeTelemetry.SnapshotTimeline(), item => item.Stage == SpeechTelemetry.FinalLatencyInstrument);
        Assert.DoesNotContain(
            RuntimeTelemetry.SnapshotTimeline(),
            item => (item.Detail ?? string.Empty).Contains("Hello there", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Duplicate_finals_do_not_create_a_second_user_turn()
    {
        var output = new CapturingSessionOutput();
        await using var runtime = Create(output, new SyntheticSpeechRecognizer(["Once"]));
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        var utterance = Guid.Parse("019944af-0000-7000-8000-0000000000f2");
        await runtime.SubmitSpeechAsync(new SpeechStarted(utterance), 0.9);
        await runtime.SubmitSpeechAsync(new SpeechFinal(utterance, "Once", 0.9), 0.9);
        await runtime.SubmitSpeechAsync(new SpeechFinal(utterance, "Once", 0.9), 0.9);
        await runtime.SubmitSpeechAsync(new SpeechEnded(utterance), 0.2);
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(1, runtime.Snapshot.Entries.Count(entry => entry.Role == ConversationRole.User));
    }

    [Fact]
    public async Task Overflow_or_gap_emits_audio_discontinuity()
    {
        var output = new CapturingSessionOutput();
        await using var runtime = Create(output, new SyntheticSpeechRecognizer());
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        Assert.True(runtime.TryAdmitAudio(Frame(1, 0)));
        Assert.False(runtime.TryAdmitAudio(Frame(3, 960)));
        await output.WaitForAsync(item => item.Payload is ErrorOutput error && error.Code == "AudioDiscontinuity");
    }

    [Fact]
    public async Task Pending_voice_does_not_start_recognition()
    {
        var output = new CapturingSessionOutput();
        var model = new ScriptedLanguageModel(releaseAfterFirstChunk: new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        await using var runtime = Create(output, new SyntheticSpeechRecognizer(), model);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Please hold the line");
        await output.WaitForAsync(item => item.Payload is TextDeltaOutput);
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.PendingMode == SessionMode.Voice);
        Assert.Null(runtime.StreamId);
        Assert.False(runtime.TryAdmitAudio(Frame(1, 0)));
    }

    [Fact]
    public async Task Pending_voice_applies_after_user_text_interrupt()
    {
        var output = new CapturingSessionOutput();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var runtime = Create(
            output,
            new SyntheticSpeechRecognizer(),
            new ScriptedLanguageModel(["AAA", "BBB"], release));
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello");
        await output.WaitForAsync(item => item.Payload is TextDeltaOutput);
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.PendingMode == SessionMode.Voice);
        await runtime.SubmitUserTextAsync("Second");
        await output.WaitForAsync(item =>
            item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice && state.PendingMode is null);
        Assert.Equal(SessionMode.Voice, runtime.Snapshot.Mode);
        Assert.Null(runtime.Snapshot.PendingMode);
        Assert.True(runtime.RecognitionActive);
        Assert.NotNull(runtime.StreamId);
        release.TrySetResult();
        await runtime.WaitUntilIdleAsync();
    }

    [Fact]
    public async Task Pending_voice_applies_after_user_barge_in_cancel()
    {
        var output = new CapturingSessionOutput();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var runtime = Create(
            output,
            new SyntheticSpeechRecognizer(),
            new ScriptedLanguageModel(["AAA", "BBB"], release));
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello");
        await output.WaitForAsync(item => item.Payload is TextDeltaOutput);
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.PendingMode == SessionMode.Voice);
        await runtime.CancelActiveResponseAsync();
        await output.WaitForAsync(item =>
            item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice && state.PendingMode is null);
        Assert.Equal(SessionMode.Voice, runtime.Snapshot.Mode);
        Assert.True(runtime.RecognitionActive);
        release.TrySetResult();
        await runtime.WaitUntilIdleAsync();
    }

    [Fact]
    public async Task Pending_voice_does_not_apply_on_disconnect()
    {
        var output = new CapturingSessionOutput();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var runtime = Create(
            output,
            new SyntheticSpeechRecognizer(),
            new ScriptedLanguageModel(["AAA", "BBB"], release));
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello");
        await output.WaitForAsync(item => item.Payload is TextDeltaOutput);
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.PendingMode == SessionMode.Voice);
        await runtime.DetachAsync();
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(SessionMode.Text, runtime.Snapshot.Mode);
        Assert.Null(runtime.Snapshot.PendingMode);
        Assert.False(runtime.RecognitionActive);
        Assert.Null(runtime.StreamId);
        release.TrySetResult();
    }

    [Fact]
    public async Task Cancelled_detach_does_not_wait_for_recognition_stop()
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new CapturingSessionOutput();
        await using var runtime = Create(output, new HoldingDisposeRecognizer(hold.Task));
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        Assert.True(runtime.RecognitionActive);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.DetachAsync(cancelled.Token));
        }
        finally
        {
            hold.TrySetResult();
        }

        await runtime.WaitUntilIdleAsync();
        Assert.False(runtime.RecognitionActive);
        Assert.Null(runtime.StreamId);
    }

    [Fact]
    public async Task Max_utterance_closes_without_finalizing()
    {
        var output = new CapturingSessionOutput();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        await using var runtime = Create(output, new SyntheticSpeechRecognizer(), time: time);
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        var utterance = Guid.Parse("019944af-0000-7000-8000-0000000000aa");
        await runtime.SubmitSpeechAsync(new SpeechStarted(utterance), 0.9);
        await runtime.SubmitSpeechAsync(new SpeechPartial(utterance, 1, "holding", 0.9), 0.9);
        await runtime.WaitUntilMailboxDrainedAsync();
        Assert.Equal(InputActivity.UserSpeaking, runtime.Input);
        var generation = runtime.MaxUtteranceGeneration;
        time.Advance(TimeSpan.FromSeconds(30));
        await runtime.SubmitTimerElapsedAsync("maxUtterance", generation, utterance);
        await output.WaitForAsync(item => item.Payload is ErrorOutput error && error.Code == "MaxUtterance");
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(InputActivity.Listening, runtime.Input);
        Assert.Null(runtime.Candidate);
        await runtime.SubmitSpeechAsync(new SpeechEnded(utterance), 0.2);
        await runtime.SubmitSpeechAsync(new SpeechFinal(utterance, "Hello there", 0.9), 0.9);
        await runtime.WaitUntilMailboxDrainedAsync();
        Assert.Equal(InputActivity.Listening, runtime.Input);
        Assert.DoesNotContain(output.Items, item => item.Payload is TranscriptFinalOutput);
    }

    [Fact]
    public async Task Max_utterance_timer_is_invalidated_after_normal_final()
    {
        var output = new CapturingSessionOutput();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        await using var runtime = Create(output, new SyntheticSpeechRecognizer(["Hello there"]), time: time);
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        var utterance = Guid.Parse("019944af-0000-7000-8000-0000000000ad");
        await runtime.SubmitSpeechAsync(new SpeechStarted(utterance), 0.9);
        await runtime.WaitUntilMailboxDrainedAsync();
        var generation = runtime.MaxUtteranceGeneration;
        await runtime.SubmitSpeechAsync(new SpeechFinal(utterance, "Hello there", 0.9), 0.9);
        await runtime.SubmitSpeechAsync(new SpeechEnded(utterance), 0.2);
        await runtime.WaitUntilMailboxDrainedAsync();
        time.Advance(TimeSpan.FromSeconds(45));
        await runtime.SubmitTimerElapsedAsync("maxUtterance", generation, utterance);
        await runtime.WaitUntilMailboxDrainedAsync();
        Assert.DoesNotContain(output.Items, item => item.Payload is ErrorOutput error && error.Code == "MaxUtterance");
    }

    [Fact]
    public async Task Max_utterance_rotates_stream_and_admits_the_next_utterance()
    {
        var output = new CapturingSessionOutput();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        await using var runtime = Create(output, new SyntheticSpeechRecognizer(), time: time);
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        var previous = runtime.StreamId;
        Assert.NotNull(previous);
        var utterance = Guid.Parse("019944af-0000-7000-8000-0000000000aa");
        await runtime.SubmitSpeechAsync(new SpeechStarted(utterance), 0.9);
        await runtime.WaitUntilMailboxDrainedAsync();
        var generation = runtime.MaxUtteranceGeneration;
        time.Advance(TimeSpan.FromSeconds(30));
        await runtime.SubmitTimerElapsedAsync("maxUtterance", generation, utterance);
        await output.WaitForAsync(item => item.Payload is ErrorOutput error && error.Code == "MaxUtterance");
        await runtime.WaitUntilIdleAsync();
        Assert.NotEqual(previous, runtime.StreamId);
        Assert.True(runtime.RecognitionActive);
        var next = Guid.Parse("019944af-0000-7000-8000-0000000000ab");
        await runtime.SubmitSpeechAsync(new SpeechStarted(next), 0.9);
        await runtime.WaitUntilMailboxDrainedAsync();
        Assert.Equal(InputActivity.UserSpeaking, runtime.Input);
    }

    [Fact]
    public async Task Speech_is_ignored_after_detach()
    {
        var output = new CapturingSessionOutput();
        await using var runtime = Create(output, new SyntheticSpeechRecognizer());
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        await runtime.DetachAsync();
        await runtime.WaitUntilIdleAsync();
        var utterance = Guid.Parse("019944af-0000-7000-8000-0000000000ac");
        await runtime.SubmitSpeechAsync(new SpeechStarted(utterance), 0.9);
        await runtime.WaitUntilMailboxDrainedAsync();
        Assert.Equal(InputActivity.Idle, runtime.Input);
        Assert.Equal(SessionStatus.Paused, runtime.Snapshot.Status);
    }

    [Fact]
    public async Task End_clears_stream_id()
    {
        var output = new CapturingSessionOutput();
        await using var runtime = Create(output, new SyntheticSpeechRecognizer());
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        Assert.NotNull(runtime.StreamId);
        Assert.True(await runtime.RequestEndAsync());
        Assert.Null(runtime.StreamId);
        Assert.False(runtime.RecognitionActive);
        var utterance = Guid.Parse("019944af-0000-7000-8000-0000000000ad");
        await runtime.SubmitSpeechAsync(new SpeechStarted(utterance), 0.9);
        await runtime.WaitUntilMailboxDrainedAsync();
        Assert.Equal(InputActivity.Idle, runtime.Input);
        Assert.Equal(SessionStatus.Ended, runtime.Snapshot.Status);
    }

    [Fact]
    public async Task Voice_unavailable_stays_in_text()
    {
        var output = new CapturingSessionOutput();
        await using var runtime = Create(output, new FailingRecognizer());
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is ErrorOutput error && error.Code == "VoiceUnavailable");
        Assert.Equal(SessionMode.Text, runtime.Snapshot.Mode);
    }

    [Fact]
    public async Task Batch_capabilities_disable_partials()
    {
        var output = new CapturingSessionOutput();
        var recognizer = new SyntheticSpeechRecognizer(
            ["Batch hello"],
            new RecognitionCapabilities(false, false, false, true));
        await using var runtime = Create(output, recognizer);
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        var utterance = Guid.Parse("019944af-0000-7000-8000-0000000000f3");
        Assert.True(runtime.TryAdmitBoundary(utterance, SpeechBoundary.Started, 0.9));
        Assert.True(runtime.TryAdmitBoundary(utterance, SpeechBoundary.Ended, 0.2));
        await output.WaitForAsync(item => item.Payload is TranscriptFinalOutput);
        Assert.DoesNotContain(output.Items, item => item.Payload is TranscriptPartialOutput);
    }

    [Fact]
    public async Task Speech_boundary_waits_for_preceding_audio_offset()
    {
        var output = new CapturingSessionOutput();
        var recognizer = new SyntheticSpeechRecognizer(["Hello there"]);
        await using var runtime = Create(output, recognizer);
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        var utterance = Guid.Parse("019944af-0000-7000-8000-0000000000f8");
        Assert.True(runtime.TryAdmitBoundary(utterance, SpeechBoundary.Started, 0.9, sampleOffset: 480));
        await Task.Delay(50);
        Assert.DoesNotContain(output.Items, item => item.Payload is TranscriptPartialOutput or TranscriptFinalOutput);
        Assert.True(runtime.TryAdmitAudio(Frame(1, 0)));
        Assert.True(runtime.TryAdmitBoundary(utterance, SpeechBoundary.Ended, 0.2, sampleOffset: 480, durationMs: 40));
        await output.WaitForAsync(item => item.Payload is TranscriptFinalOutput);
    }

    [Fact]
    public async Task Saturated_input_queue_latches_stream_and_rejects_boundaries()
    {
        var output = new CapturingSessionOutput();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var runtime = Create(output, new BlockingSpeechRecognizer(hold.Task));
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        var previous = runtime.StreamId;
        Assert.NotNull(previous);
        var utterance = Guid.Parse("019944af-0000-7000-8000-0000000000f9");
        var latched = false;
        for (var index = 1; index <= 80 && !latched; index++)
        {
            runtime.TryAdmitAudio(Frame(index, (index - 1) * 480));
            latched = !runtime.TryAdmitBoundary(utterance, SpeechBoundary.Started, 0.9);
        }

        Assert.True(latched);
        hold.TrySetResult();
        await output.WaitForAsync(item => item.Payload is ErrorOutput error && error.Code == "AudioDiscontinuity");
        Assert.False(runtime.TryAdmitBoundary(utterance, SpeechBoundary.Ended, 0.2, streamId: previous));
    }

    [Fact]
    public void Saturated_input_queue_rejects_overflow_writes()
    {
        var ingress = new AudioIngress();
        var accepted = 0;
        for (var index = 0; index < 30; index++)
        {
            if (ingress.TryWrite(new IngressAudio(Frame(index + 1, index * 480))))
            {
                accepted++;
            }
        }

        Assert.Equal(25, accepted);
    }

    [Fact]
    public async Task Stale_stream_id_does_not_enter_a_rotated_recognizer()
    {
        var output = new CapturingSessionOutput();
        await using var runtime = Create(output, new SyntheticSpeechRecognizer(["Hello there"]));
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        var previous = runtime.StreamId;
        Assert.NotNull(previous);
        await runtime.SetMutedAsync(true);
        await runtime.WaitUntilMailboxDrainedAsync();
        await runtime.SetMutedAsync(false);
        await runtime.WaitUntilMailboxDrainedAsync();
        Assert.NotEqual(previous, runtime.StreamId);
        Assert.False(runtime.TryAdmitAudio(Frame(1, 0), previous));
        Assert.True(runtime.TryAdmitAudio(Frame(1, 0), runtime.StreamId));
    }

    [Fact]
    public async Task Voice_reapply_rejects_new_stream_id_until_new_session_is_installed()
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new CapturingSessionOutput();
        await using var runtime = Create(output, new HoldingDisposeRecognizer(hold.Task));
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        var previous = runtime.StreamId;
        Assert.NotNull(previous);
        Assert.True(runtime.TryAdmitAudio(Frame(1, 0), previous));

        try
        {
            await runtime.SetModeAsync(SessionMode.Voice);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (runtime.StreamId == previous)
            {
                timeout.Token.ThrowIfCancellationRequested();
                await Task.Yield();
            }

            var next = runtime.StreamId;
            Assert.NotNull(next);
            Assert.False(runtime.TryAdmitAudio(Frame(1, 0), next));
            Assert.False(runtime.TryAdmitAudio(Frame(2, 480), previous));
            hold.TrySetResult();
            await runtime.WaitUntilMailboxDrainedAsync();
            Assert.True(runtime.TryAdmitAudio(Frame(1, 0), next));
        }
        finally
        {
            hold.TrySetResult();
        }
    }

    private static AudioFrame Frame(long sequence, long offset) =>
        new(sequence, offset, new byte[960]);

    private static SessionRuntime Create(
        ISessionOutput output,
        ISpeechRecognizer recognizer,
        ILanguageModel? model = null,
        FakeTimeProvider? time = null,
        InteractionPolicy? policy = null)
    {
        time ??= new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        policy ??= new InteractionPolicy(MaxUtteranceSeconds: 30);
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 64).Select(index => Guid.Parse($"019944af-0000-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842")]);
        var store = new InMemoryMemoryStore();
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
            policy: policy,
            recognizer: recognizer);
    }

    private sealed class BlockingSpeechRecognizer(Task hold) : ISpeechRecognizer
    {
        public RecognitionCapabilities Capabilities { get; } = new(true, true, true, true);

        public ValueTask<ISpeechRecognitionSession> OpenAsync(
            RecognitionOptions options,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ISpeechRecognitionSession>(new BlockingSpeechSession(hold));

        private sealed class BlockingSpeechSession(Task hold) : ISpeechRecognitionSession
        {
            public async ValueTask PushAudioAsync(AudioFrame frame, CancellationToken cancellationToken = default) =>
                await hold.WaitAsync(cancellationToken).ConfigureAwait(false);

            public ValueTask ObserveBoundaryAsync(
                Guid utteranceId,
                SpeechBoundary boundary,
                CancellationToken cancellationToken = default) =>
                ValueTask.CompletedTask;

            public ValueTask CompleteInputAsync(CancellationToken cancellationToken = default) =>
                ValueTask.CompletedTask;

            public async IAsyncEnumerable<SpeechRecognitionEvent> ReadEventsAsync(
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.CompletedTask;
                yield break;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class HoldingDisposeRecognizer(Task hold, TaskCompletionSource? disposing = null) : ISpeechRecognizer
    {
        public RecognitionCapabilities Capabilities { get; } = new(true, true, true, true);

        public ValueTask<ISpeechRecognitionSession> OpenAsync(
            RecognitionOptions options,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ISpeechRecognitionSession>(new HoldingDisposeSession(hold, disposing));

        private sealed class HoldingDisposeSession(Task hold, TaskCompletionSource? disposing) : ISpeechRecognitionSession
        {
            public ValueTask PushAudioAsync(AudioFrame frame, CancellationToken cancellationToken = default) =>
                ValueTask.CompletedTask;

            public ValueTask ObserveBoundaryAsync(
                Guid utteranceId,
                SpeechBoundary boundary,
                CancellationToken cancellationToken = default) =>
                ValueTask.CompletedTask;

            public ValueTask CompleteInputAsync(CancellationToken cancellationToken = default) =>
                ValueTask.CompletedTask;

            public async IAsyncEnumerable<SpeechRecognitionEvent> ReadEventsAsync(
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.CompletedTask;
                yield break;
            }

            public async ValueTask DisposeAsync()
            {
                disposing?.TrySetResult();
                await hold.ConfigureAwait(false);
            }
        }
    }

    private sealed class FailingRecognizer : ISpeechRecognizer
    {
        public RecognitionCapabilities Capabilities { get; } = new(true, true, true, true);

        public ValueTask<ISpeechRecognitionSession> OpenAsync(
            RecognitionOptions options,
            CancellationToken cancellationToken = default) =>
            throw AgentCoreErrors.VoiceUnavailable();
    }

}
