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

public sealed class SpeechPlaybackTests
{
    [Fact]
    public async Task Playback_starts_before_model_completion_and_waits_for_ack()
    {
        var output = new CapturingSessionOutput();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new ScriptedLanguageModel(
            ["There are three points. ", "Later text continues."],
            releaseAfterFirstChunk: release);
        var synthesizer = new CountingSynthesizer();
        await using var runtime = Create(output, model, synthesizer);
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        await runtime.SubmitUserTextAsync("Hello");
        var audio = await output.WaitForAsync(item => item.Payload is AudioFrameOutput);
        Assert.DoesNotContain(output.Items, item => item.Payload is ResponseCompletedOutput completed && completed.InterruptReason is null && !completed.Failed);
        Assert.True(synthesizer.Requests >= 1);
        Assert.True(runtime.TtsJobsStarted >= 1);
        release.TrySetResult();
        var final = await output.WaitForAsync(item => item.Payload is AudioFrameOutput frame && frame.IsFinal);
        var responseId = audio.ResponseId!.Value;
        Assert.Equal(1, ((AudioFrameOutput)audio.Payload).FrameSequence);
        await runtime.SubmitPlaybackAsync(responseId, "started", 0, 0);
        await runtime.SubmitPlaybackAsync(responseId, "completed", runtime.SentSamples, 0);
        await runtime.WaitUntilIdleAsync();
        var terminal = Assert.Single(output.Terminals, item => item.InterruptReason is null);
        Assert.False(terminal.Failed);
        Assert.True(runtime.TryAdmitAudio(new AudioFrame(1, 0, new byte[960])));
    }

    [Fact]
    public async Task Empty_final_marker_does_not_advance_sample_offset()
    {
        var output = new CapturingSessionOutput();
        await using var runtime = Create(output, new ScriptedLanguageModel(["   "]));
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        await runtime.SubmitUserTextAsync("Hello");
        var final = await output.WaitForAsync(item => item.Payload is AudioFrameOutput frame && frame.IsFinal);
        var marker = (AudioFrameOutput)final.Payload;
        Assert.Empty(marker.Data);
        Assert.Equal(0, marker.SampleOffset);
        await runtime.SubmitPlaybackAsync(final.ResponseId!.Value, "completed", 0, 0);
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(0, runtime.OutputSampleOffset);
    }

    [Fact]
    public async Task Playback_completed_before_final_samples_does_not_credit_unplayed_text()
    {
        var output = new CapturingSessionOutput();
        await using var runtime = Create(output, new ScriptedLanguageModel(["There are three points."]));
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        await runtime.SubmitUserTextAsync("Hello");
        var final = await output.WaitForAsync(item => item.Payload is AudioFrameOutput frame && frame.IsFinal);
        var responseId = final.ResponseId!.Value;
        var sent = runtime.SentSamples;
        Assert.True(sent > 2);
        await runtime.SubmitPlaybackAsync(responseId, "started", 0, 0);
        await runtime.SubmitPlaybackAsync(responseId, "completed", sent / 2, 0);
        await runtime.WaitUntilIdleAsync();
        Assert.DoesNotContain(output.Terminals, item => item.InterruptReason is null);
        var assistant = runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant);
        Assert.True(assistant.HeardTextEndExclusive < assistant.Text.Length);
        await runtime.SubmitPlaybackAsync(responseId, "completed", sent, 0);
        await runtime.WaitUntilIdleAsync();
        var terminal = Assert.Single(output.Terminals, item => item.InterruptReason is null);
        Assert.False(terminal.Failed);
        Assert.Equal(
            runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant).HeardTextEndExclusive,
            terminal.HeardTextEndExclusive);
    }

    [Fact]
    public async Task Unacked_output_stops_at_two_seconds()
    {
        var output = new CapturingSessionOutput();
        var text = new string('a', 160) + ".";
        await using var runtime = Create(output, new ScriptedLanguageModel([text]));
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        await runtime.SubmitUserTextAsync("Hello");
        await output.WaitForAsync(_ =>
            output.Items.Select(item => item.Payload).OfType<AudioFrameOutput>().Count() >= 90);
        Assert.True(runtime.SentSamples <= CanonicalAudio.SampleRateHz * 2);
        await runtime.SubmitPlaybackAsync(runtime.ActiveResponseId!.Value, "progress", runtime.SentSamples, 0);
        var final = await output.WaitForAsync(item => item.Payload is AudioFrameOutput frame && frame.IsFinal);
        await runtime.SubmitPlaybackAsync(final.ResponseId!.Value, "completed", runtime.SentSamples, 0);
        await runtime.WaitUntilIdleAsync();
    }

    [Fact]
    public async Task Supersede_before_release_starts_no_further_tts_job()
    {
        var output = new CapturingSessionOutput();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new ScriptedLanguageModel(
            ["There are three points. ", "This second sentence should not speak."],
            releaseAfterFirstChunk: release);
        var synthesizer = new CountingSynthesizer();
        await using var runtime = Create(output, model, synthesizer);
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        await runtime.SubmitUserTextAsync("Hello");
        await output.WaitForAsync(item => item.Payload is AudioFrameOutput);
        var jobs = runtime.TtsJobsStarted;
        await runtime.CancelActiveResponseAsync();
        release.TrySetResult();
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(jobs, runtime.TtsJobsStarted);
        Assert.Equal(1, jobs);
        Assert.DoesNotContain(output.Items, item => item.Payload is AudioFrameOutput audio && audio.IsFinal);
    }

    private static SessionRuntime Create(
        ISessionOutput output,
        ILanguageModel model,
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
            model,
            new DefaultAgentBrain(new PromptContextBuilder()),
            store,
            output,
            ids,
            time,
            NullLogger<SessionRuntime>.Instance,
            recognizer: new SyntheticSpeechRecognizer(),
            synthesizer: synthesizer ?? new SyntheticSpeechSynthesizer());
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
