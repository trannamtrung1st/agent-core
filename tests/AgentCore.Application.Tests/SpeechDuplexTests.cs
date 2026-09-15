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
using System.Runtime.CompilerServices;

namespace AgentCore.Application.Tests;

public sealed class SpeechDuplexTests
{
    [Fact]
    public async Task Transcripts_continue_while_unacked_output_plays()
    {
        var output = new CapturingSessionOutput();
        var recognizer = new SyntheticSpeechRecognizer(["While speaking"]);
        await using var runtime = Create(output, recognizer);
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        await runtime.SubmitUserTextAsync("Hello");
        var audio = await output.WaitForAsync(item => item.Payload is AudioFrameOutput);
        Assert.True(runtime.RecognitionActive);
        Assert.True(runtime.Output is OutputActivity.AgentGenerating or OutputActivity.AgentSpeaking);
        await runtime.SubmitPlaybackAsync(audio.ResponseId!.Value, "started", 0, 0);
        await runtime.WaitUntilMailboxDrainedAsync();

        var utterance = Guid.Parse("019944af-0000-7000-8000-0000000000d1");
        Assert.True(runtime.TryAdmitAudio(Frame(1, 0)));
        Assert.True(runtime.TryAdmitBoundary(utterance, SpeechBoundary.Started, 0.9));
        Assert.True(runtime.TryAdmitBoundary(utterance, SpeechBoundary.Ended, 0.2));
        await output.WaitForAsync(item => item.Payload is TranscriptPartialOutput);
        await output.WaitForAsync(item => item.Payload is TranscriptFinalOutput);
        Assert.True(runtime.RecognitionActive);
        Assert.True(runtime.Input is InputActivity.Listening or InputActivity.Finalizing or InputActivity.UserSpeaking);
        Assert.Contains(output.Items, item => item.Payload is AudioFrameOutput);
        await runtime.WaitUntilMailboxDrainedAsync();
    }

    [Fact]
    public async Task Slow_tts_does_not_block_stt()
    {
        var output = new CapturingSessionOutput();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recognizer = new SyntheticSpeechRecognizer(["Independent"]);
        await using var runtime = Create(output, recognizer, new GatedSynthesizer(gate));
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        await runtime.SubmitUserTextAsync("Hello");
        await output.WaitForAsync(item => item.Payload is TextDeltaOutput);

        var utterance = Guid.Parse("019944af-0000-7000-8000-0000000000d2");
        Assert.True(runtime.TryAdmitAudio(Frame(1, 0)));
        Assert.True(runtime.TryAdmitBoundary(utterance, SpeechBoundary.Started, 0.9));
        Assert.True(runtime.TryAdmitBoundary(utterance, SpeechBoundary.Ended, 0.2));
        await output.WaitForAsync(item => item.Payload is TranscriptFinalOutput);
        Assert.DoesNotContain(output.Items, item => item.Payload is AudioFrameOutput);

        gate.TrySetResult();
        await output.WaitForAsync(item => item.Payload is AudioFrameOutput);
        await runtime.SubmitPlaybackAsync(runtime.ActiveResponseId!.Value, "completed", runtime.SentSamples, 0);
        await runtime.WaitUntilIdleAsync();
    }

    [Fact]
    public async Task Mute_stops_ingress_without_stopping_playback()
    {
        var output = new CapturingSessionOutput();
        await using var runtime = Create(output, new SyntheticSpeechRecognizer(["Ignored"]));
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        var streamBefore = runtime.StreamId;
        await runtime.SubmitUserTextAsync("Hello");
        var audio = await output.WaitForAsync(item => item.Payload is AudioFrameOutput);
        await runtime.SetMutedAsync(true);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Muted);
        Assert.True(runtime.Muted);
        Assert.False(runtime.TryAdmitAudio(Frame(1, 0)));
        Assert.False(runtime.TryAdmitBoundary(Guid.NewGuid(), SpeechBoundary.Started, 0.9));
        await runtime.WaitUntilMailboxDrainedAsync();
        Assert.DoesNotContain(output.Items, item => item.Payload is ErrorOutput error && error.Code == "AudioDiscontinuity");
        await runtime.SubmitPlaybackAsync(audio.ResponseId!.Value, "started", 0, 0);
        await runtime.SubmitPlaybackAsync(audio.ResponseId!.Value, "completed", runtime.SentSamples, 0);
        await runtime.WaitUntilIdleAsync();

        await runtime.SetMutedAsync(false);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && !state.Muted && state.StreamId != streamBefore);
        Assert.NotEqual(streamBefore, runtime.StreamId);
        Assert.True(runtime.TryAdmitAudio(Frame(1, 0)));
    }

    [Fact]
    public async Task Detach_stops_recognition_and_further_tts()
    {
        var output = new CapturingSessionOutput();
        var synthesizer = new CountingSynthesizer();
        await using var runtime = Create(output, new SyntheticSpeechRecognizer(), synthesizer);
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        Assert.True(runtime.RecognitionActive);
        await runtime.SubmitUserTextAsync("Hello");
        await output.WaitForAsync(item => item.Payload is AudioFrameOutput);
        var jobs = runtime.TtsJobsStarted;
        await runtime.DetachAsync();
        await runtime.WaitUntilIdleAsync();
        Assert.False(runtime.RecognitionActive);
        Assert.False(runtime.TryAdmitAudio(Frame(1, 0)));
        Assert.Equal(jobs, runtime.TtsJobsStarted);
        Assert.Equal(jobs, synthesizer.Requests);
    }

    private static AudioFrame Frame(long sequence, long offset) =>
        new(sequence, offset, new byte[960]);

    private static SessionRuntime Create(
        ISessionOutput output,
        ISpeechRecognizer recognizer,
        ISpeechSynthesizer? synthesizer = null)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 128).Select(index => Guid.Parse($"019944af-0000-7000-8000-{index:D12}")),
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
            new ScriptedLanguageModel(["There are three points. "]),
            new DefaultAgentBrain(new PromptContextBuilder()),
            store,
            output,
            ids,
            time,
            NullLogger<SessionRuntime>.Instance,
            recognizer: recognizer,
            synthesizer: synthesizer ?? new SyntheticSpeechSynthesizer());
    }

    private sealed class GatedSynthesizer(TaskCompletionSource gate) : ISpeechSynthesizer
    {
        private readonly SyntheticSpeechSynthesizer _inner = new();

        public SynthesisCapabilities Capabilities => _inner.Capabilities;

        public async IAsyncEnumerable<SpeechSynthesisEvent> SynthesizeAsync(
            SpeechRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            await foreach (var item in _inner.SynthesizeAsync(request, cancellationToken))
            {
                yield return item;
            }
        }
    }

    private sealed class CountingSynthesizer : ISpeechSynthesizer
    {
        private readonly SyntheticSpeechSynthesizer _inner = new();

        public int Requests { get; private set; }

        public SynthesisCapabilities Capabilities => _inner.Capabilities;

        public IAsyncEnumerable<SpeechSynthesisEvent> SynthesizeAsync(
            SpeechRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests++;
            return _inner.SynthesizeAsync(request, cancellationToken);
        }
    }
}
