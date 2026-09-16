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

public sealed class BargeInTests
{
    [Fact]
    public async Task Wait_emits_stop_then_interrupted_and_keeps_stt()
    {
        var harness = await LiveVoiceAsync();
        var r1 = harness.Runtime.ActiveResponseId!.Value;
        await harness.Output.WaitForAsync(item => item.Payload is AudioFrameOutput);
        var utterance = Guid.Parse("019944af-0000-7000-8000-0000000000c1");
        await harness.Runtime.SubmitSpeechAsync(new SpeechStarted(utterance), 0.95);
        await harness.Runtime.SubmitSpeechAsync(new SpeechPartial(utterance, 1, "wait", 0.95), 0.95);
        await harness.Output.WaitForAsync(item => item.Payload is ResponseCompletedOutput);
        await harness.Runtime.WaitUntilMailboxDrainedAsync();
        var payloads = harness.Output.Items.Select(item => item.Payload).ToArray();
        var stop = Array.FindIndex(payloads, item => item is PlaybackStopOutput);
        var interrupted = Array.FindIndex(payloads, item => item is ResponseCompletedOutput completed && completed.InterruptReason is not null);
        Assert.True(stop >= 0 && interrupted > stop);
        Assert.True(harness.Runtime.RecognitionActive);
        Assert.Contains(harness.Snapshot.Entries, entry => entry.ResponseId == r1 && entry.Status == EntryStatus.Interrupted);
        await harness.Runtime.DisposeAsync();
    }

    [Fact]
    public async Task Late_r1_playback_does_not_change_heard_after_r2()
    {
        var harness = await LiveVoiceAsync();
        var r1 = harness.Runtime.ActiveResponseId!.Value;
        await harness.Output.WaitForAsync(item => item.Payload is AudioFrameOutput);
        await harness.Runtime.SubmitPlaybackAsync(r1, "progress", Math.Min(480, harness.Runtime.SentSamples), 0);
        await harness.Runtime.WaitUntilMailboxDrainedAsync();
        var heard = harness.Snapshot.Entries.Single(entry => entry.ResponseId == r1).HeardTextEndExclusive;
        var utterance = Guid.Parse("019944af-0000-7000-8000-0000000000c2");
        await harness.Runtime.SubmitSpeechAsync(new SpeechStarted(utterance), 0.95);
        await harness.Runtime.SubmitSpeechAsync(new SpeechPartial(utterance, 1, "wait", 0.95), 0.95);
        await harness.Runtime.WaitUntilMailboxDrainedAsync();
        await harness.Runtime.SubmitPlaybackAsync(r1, "started", harness.Runtime.SentSamples, 99);
        await harness.Runtime.SubmitPlaybackAsync(r1, "progress", harness.Runtime.SentSamples, 99);
        await harness.Runtime.SubmitPlaybackAsync(r1, "completed", harness.Runtime.SentSamples, 99);
        await harness.Runtime.SubmitPlaybackAsync(r1, "stopped", harness.Runtime.SentSamples, 99);
        await harness.Runtime.WaitUntilMailboxDrainedAsync();
        Assert.Equal(heard, harness.Snapshot.Entries.Single(entry => entry.ResponseId == r1).HeardTextEndExclusive);
        await harness.Runtime.DisposeAsync();
    }

    [Fact]
    public async Task No_timing_marks_exclude_partial_segment_from_next_prompt()
    {
        var output = new CapturingSessionOutput();
        var time = Clock();
        var brain = new RecordingAgentBrain(new DefaultAgentBrain(new PromptContextBuilder()));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new ScriptedLanguageModel(
            ["There are three points. ", "Later text continues."],
            releaseAfterFirstChunk: release);
        release.TrySetResult();
        await using var runtime = Create(output, model, time, brain, new UntimedSynthesizer());
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        await runtime.SubmitUserTextAsync("Hello");
        var first = "There are three points. ".Length * UntimedSynthesizer.SamplesPerCharacter;
        var second = "Later text continues.".Length * UntimedSynthesizer.SamplesPerCharacter;
        var consumed = first + (second / 2);
        await output.WaitForAsync(_ => runtime.SentSamples >= consumed);
        await runtime.WaitUntilMailboxDrainedAsync();
        var r1 = runtime.ActiveResponseId!.Value;
        Assert.True(consumed < first + second);
        await runtime.SubmitPlaybackAsync(r1, "progress", consumed, 0);
        await runtime.WaitUntilMailboxDrainedAsync();
        var utterance = Guid.Parse("019944af-0000-7000-8000-0000000000c3");
        await runtime.SubmitSpeechAsync(new SpeechStarted(utterance), 0.95);
        await runtime.SubmitSpeechAsync(new SpeechFinal(utterance, "Wait, what did you mean?", 0.95), 0.95);
        await output.WaitForAsync(item => item.Payload is ResponseCompletedOutput completed && completed.InterruptReason is not null);
        await output.WaitForAsync(item => item.ResponseId != r1 && item.Payload is ResponseStartedOutput);
        Assert.NotNull(brain.LastContext);
        var prompt = string.Join('\n', brain.LastContext!.History.Select(PromptContextBuilder.EligibleAssistantText));
        Assert.Contains("There are three points.", prompt);
        Assert.DoesNotContain("Later text continues.", prompt);
        var heard = runtime.Snapshot.Entries.Single(entry => entry.ResponseId == r1).HeardTextEndExclusive;
        Assert.True(heard > 0);
        Assert.True(heard < runtime.Snapshot.Entries.Single(entry => entry.ResponseId == r1).Text.Length);
    }

    [Fact]
    public async Task Speech_activity_fallback_interrupts_at_degraded_deadline()
    {
        var output = new CapturingSessionOutput();
        var time = Clock();
        var model = new GatedThenLiveModel();
        await using var runtime = Create(
            output,
            model,
            time,
            new RecordingAgentBrain(new DefaultAgentBrain(new PromptContextBuilder())),
            new SyntheticSpeechSynthesizer(),
            new InteractionPolicy(BargeInPolicy: "speechActivity"));
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        await runtime.SubmitUserTextAsync("Hello");
        await output.WaitForAsync(item => item.Payload is TextDeltaOutput);
        var utterance = Guid.Parse("019944af-0000-7000-8000-0000000000c4");
        await runtime.SubmitSpeechAsync(new SpeechStarted(utterance), 0.95);
        await runtime.WaitUntilMailboxDrainedAsync();
        time.Advance(TimeSpan.FromMilliseconds(250));
        await runtime.SubmitTimerElapsedAsync("candidate", runtime.TimerGeneration, utterance);
        await output.WaitForAsync(item => item.Payload is ResponseCompletedOutput);
        await runtime.WaitUntilMailboxDrainedAsync();
        Assert.Null(runtime.ActiveResponseId);
        model.Gate.TrySetResult();
        await runtime.WaitUntilMailboxDrainedAsync();
    }

    [Fact]
    public async Task Interrupt_before_first_token_starts_no_r1_text()
    {
        var output = new CapturingSessionOutput();
        var time = Clock();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new HoldFirstModel(gate);
        await using var runtime = Create(output, model, time, new RecordingAgentBrain(new DefaultAgentBrain(new PromptContextBuilder())));
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        await runtime.SubmitUserTextAsync("Hello");
        await output.WaitForAsync(item => item.Payload is ResponseStartedOutput);
        var utterance = Guid.Parse("019944af-0000-7000-8000-0000000000c5");
        await runtime.SubmitSpeechAsync(new SpeechStarted(utterance), 0.95);
        await runtime.SubmitSpeechAsync(new SpeechPartial(utterance, 1, "wait", 0.95), 0.95);
        await runtime.WaitUntilMailboxDrainedAsync();
        gate.TrySetResult();
        await runtime.WaitUntilMailboxDrainedAsync();
        Assert.DoesNotContain(output.TextDeltas, delta => delta.Text.Contains("hidden", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sequence_gap_during_tts_does_not_barge_in()
    {
        var harness = await LiveVoiceAsync();
        await harness.Output.WaitForAsync(item => item.Payload is AudioFrameOutput);
        var utterance = Guid.Parse("019944af-0000-7000-8000-0000000000c6");
        await harness.Runtime.SubmitSpeechAsync(new SpeechStarted(utterance), 0.95);
        await harness.Runtime.WaitUntilMailboxDrainedAsync();
        Assert.Equal(InputActivity.UserSpeaking, harness.Runtime.Input);
        var scheduled = harness.Runtime.TimerGeneration;
        Assert.True(harness.Runtime.TryAdmitAudio(Frame(1, 0)));
        Assert.False(harness.Runtime.TryAdmitAudio(Frame(3, 960)));
        await harness.Output.WaitForAsync(item => item.Payload is ErrorOutput error && error.Code == "AudioDiscontinuity");
        await harness.Runtime.WaitUntilIdleAsync();
        Assert.Equal(InputActivity.Listening, harness.Runtime.Input);
        Assert.Null(harness.Runtime.Candidate);
        harness.Time.Advance(TimeSpan.FromMilliseconds(500));
        await harness.Runtime.SubmitTimerElapsedAsync("candidate", scheduled, utterance);
        await harness.Runtime.WaitUntilMailboxDrainedAsync();
        Assert.NotNull(harness.Runtime.ActiveResponseId);
        Assert.DoesNotContain(
            harness.Output.Items,
            item => item.Payload is ResponseCompletedOutput completed && completed.InterruptReason is not null);
        await harness.Runtime.DisposeAsync();
    }

    private static AudioFrame Frame(long sequence, long offset) =>
        new(sequence, offset, new byte[960]);

    private static async Task<VoiceHarness> LiveVoiceAsync()
    {
        var time = Clock();
        var output = new CapturingSessionOutput();
        var model = new ScriptedLanguageModel(["There are three points. "]);
        var runtime = Create(output, model, time, new RecordingAgentBrain(new DefaultAgentBrain(new PromptContextBuilder())));
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        await runtime.SubmitUserTextAsync("Hello");
        await output.WaitForAsync(item => item.Payload is TextDeltaOutput);
        return new VoiceHarness(runtime, time, output);
    }

    private static FakeTimeProvider Clock() =>
        new(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));

    private static SessionRuntime Create(
        ISessionOutput output,
        ILanguageModel model,
        FakeTimeProvider time,
        IAgentBrain brain,
        ISpeechSynthesizer? synthesizer = null,
        InteractionPolicy? policy = null)
    {
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 256).Select(index => Guid.Parse($"019944af-0000-7000-8000-{index:D12}")),
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
            model,
            brain,
            store,
            output,
            ids,
            time,
            NullLogger<SessionRuntime>.Instance,
            new FakeInterruptionClassifier(),
            policy: policy,
            recognizer: new SyntheticSpeechRecognizer(),
            synthesizer: synthesizer ?? new SyntheticSpeechSynthesizer());
    }

    private sealed record VoiceHarness(SessionRuntime Runtime, FakeTimeProvider Time, CapturingSessionOutput Output)
    {
        public SessionSnapshot Snapshot => Runtime.Snapshot;
    }

    private sealed class HoldFirstModel(TaskCompletionSource gate) : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(true, true);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            yield return new ModelTextDelta("R1 hidden");
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }

    private sealed class UntimedSynthesizer : ISpeechSynthesizer
    {
        public const int SamplesPerCharacter = CanonicalAudio.FrameSamples20Ms;

        public SynthesisCapabilities Capabilities { get; } = new(
            true,
            TimingMarks: false,
            true,
            false,
            true,
            [CanonicalAudio.Format]);

        public async IAsyncEnumerable<SpeechSynthesisEvent> SynthesizeAsync(
            SpeechRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var units = Math.Max(1, request.Text.Trim().Length);
            var totalSamples = (long)units * SamplesPerCharacter;
            long offset = 0;
            long frameSequence = 1;
            while (offset < totalSamples)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var samples = (int)Math.Min(CanonicalAudio.FrameSamples20Ms, totalSamples - offset);
                yield return new SpeechAudio(new AudioFrame(frameSequence, offset, new byte[samples * 2]));
                offset += samples;
                frameSequence++;
                await Task.Yield();
            }

            yield return new SpeechSynthesisCompleted(offset);
        }
    }
}
