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

    private static AudioFrame Frame(long sequence, long offset) =>
        new(sequence, offset, new byte[960]);

    private static SessionRuntime Create(
        ISessionOutput output,
        ISpeechRecognizer recognizer,
        ILanguageModel? model = null)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
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
            recognizer: recognizer);
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
