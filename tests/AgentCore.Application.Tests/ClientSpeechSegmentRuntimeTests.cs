using AgentCore.Application.Agents;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Speech;
using AgentCore.Application.Testing;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class ClientSpeechSegmentRuntimeTests
{
    [Fact]
    public async Task Client_speech_emits_ordered_segments_before_model_completion()
    {
        var output = new CapturingSessionOutput();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var runtime = Create(output, new ScriptedLanguageModel(ScriptedLanguageModel.LongerChunks, hold));
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        await runtime.SubmitUserTextAsync("Hello");
        var first = await output.WaitForAsync(item => item.Payload is SpeechOutputSegmentOutput);
        var firstSegment = (SpeechOutputSegmentOutput)first.Payload;
        Assert.Equal(0, firstSegment.SegmentIndex);
        Assert.Equal(0, firstSegment.TextStart);
        Assert.Contains("three points", firstSegment.Text, StringComparison.Ordinal);
        Assert.Equal("default", firstSegment.VoiceHint);
        Assert.Equal("en", firstSegment.Language);
        Assert.Equal(1.0, firstSegment.SpeakingRate);
        Assert.DoesNotContain(output.Items, item => item.Payload is ResponseCompletedOutput);
        hold.TrySetResult();
        var speechCompleted = await output.WaitForAsync(item => item.Payload is SpeechOutputCompletedOutput);
        await AckClientSpeechAsync(runtime, speechCompleted);
        await runtime.WaitUntilIdleAsync();
        var segments = output.Items.Select(item => item.Payload).OfType<SpeechOutputSegmentOutput>().ToArray();
        Assert.True(segments.Length >= 2);
        for (var index = 1; index < segments.Length; index++)
        {
            Assert.Equal(segments[index - 1].SegmentIndex + 1, segments[index].SegmentIndex);
            Assert.Equal(first.ResponseId, output.Items.First(item => item.Payload == segments[index]).ResponseId);
        }

        var completed = Assert.Single(output.Items.Select(item => item.Payload).OfType<SpeechOutputCompletedOutput>());
        Assert.Equal(segments[^1].TextStart + segments[^1].Text.Length, completed.TextEndExclusive);
        Assert.Contains(output.Items, item => item.Payload is ResponseCompletedOutput);
        Assert.DoesNotContain(output.Items, item => item.Payload is AudioFrameOutput);
        Assert.NotEqual(
            typeof(SpeechOutputCompletedOutput),
            output.Items.First(item => item.Payload is ResponseCompletedOutput).Payload.GetType());
    }

    [Fact]
    public async Task Explicit_speech_text_overrides_display_markdown()
    {
        var output = new CapturingSessionOutput();
        await using var runtime = Create(
            output,
            new ScriptedLanguageModel(["Shown **bold**. [[speech:Order 91 is delayed.]]"]));
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        await runtime.SubmitUserTextAsync("status");
        var speechCompleted = await output.WaitForAsync(item => item.Payload is SpeechOutputCompletedOutput);
        await AckClientSpeechAsync(runtime, speechCompleted);
        await runtime.WaitUntilIdleAsync();
        var spoken = string.Concat(output.Items.Select(item => item.Payload).OfType<SpeechOutputSegmentOutput>().Select(segment => segment.Text));
        Assert.Contains("Order 91 is delayed.", spoken, StringComparison.Ordinal);
        Assert.DoesNotContain("**", spoken, StringComparison.Ordinal);
        var assistant = runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant);
        Assert.Contains("**bold**", assistant.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Display_markdown_is_not_spoken_as_raw_syntax()
    {
        var output = new CapturingSessionOutput();
        await using var runtime = Create(output, new ScriptedLanguageModel(["The architecture has **three** pieces."]));
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        await runtime.SubmitUserTextAsync("markdown");
        var speechCompleted = await output.WaitForAsync(item => item.Payload is SpeechOutputCompletedOutput);
        await AckClientSpeechAsync(runtime, speechCompleted);
        await runtime.WaitUntilIdleAsync();
        var spoken = string.Concat(output.Items.Select(item => item.Payload).OfType<SpeechOutputSegmentOutput>().Select(segment => segment.Text));
        Assert.Contains("three", spoken, StringComparison.Ordinal);
        Assert.DoesNotContain("**", spoken, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Server_audio_path_does_not_emit_client_speech_events()
    {
        var output = new CapturingSessionOutput();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 64).Select(index => Guid.Parse($"019944af-00c2-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940c002")]);
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
        await store.SaveAsync(snapshot, 0);
        await using var runtime = new SessionRuntime(
            snapshot,
            new ScriptedLanguageModel(),
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
        var final = await output.WaitForAsync(item => item.Payload is AudioFrameOutput frame && frame.IsFinal);
        await runtime.SubmitPlaybackAsync(final.ResponseId!.Value, "started", 0, 0);
        await runtime.SubmitPlaybackAsync(final.ResponseId!.Value, "completed", runtime.SentSamples, 0);
        await runtime.WaitUntilIdleAsync();
        Assert.DoesNotContain(output.Items, item => item.Payload is SpeechOutputSegmentOutput);
        Assert.DoesNotContain(output.Items, item => item.Payload is SpeechOutputCompletedOutput);
        Assert.Contains(output.Items, item => item.Payload is AudioFrameOutput);
    }

    [Fact]
    public async Task Completion_waits_for_client_speech_playback_ack()
    {
        var output = new CapturingSessionOutput();
        await using var runtime = Create(output, new ScriptedLanguageModel(["Hello from synthetic."]));
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        await runtime.SubmitUserTextAsync("Hello");
        var speechCompleted = await output.WaitForAsync(item => item.Payload is SpeechOutputCompletedOutput);
        await runtime.WaitUntilMailboxDrainedAsync();
        Assert.DoesNotContain(output.Items, item => item.Payload is ResponseCompletedOutput);
        Assert.False(await runtime.SubmitPlaybackAsync(speechCompleted.ResponseId!.Value, "started", 1, 0));
        Assert.False(await runtime.SubmitPlaybackAsync(speechCompleted.ResponseId!.Value, "completed", 0, 99));
        await AckClientSpeechAsync(runtime, speechCompleted);
        await runtime.WaitUntilIdleAsync();
        Assert.Contains(output.Items, item => item.Payload is ResponseCompletedOutput terminal && !terminal.Failed);
        var assistant = runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant);
        var end = Assert.IsType<SpeechOutputCompletedOutput>(speechCompleted.Payload).TextEndExclusive;
        Assert.Equal(end, assistant.HeardTextEndExclusive);
    }

    [Fact]
    public async Task Successful_playback_dispatches_queued_text_but_stop_does_not()
    {
        var output = new CapturingSessionOutput();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var runtime = Create(output, new ScriptedLanguageModel(["Hello from ", "synthetic."], hold));
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        await runtime.SubmitUserTextAsync("Hello");
        await output.WaitForAsync(item => item.Payload is TextDeltaOutput);
        Assert.True(await runtime.SubmitUserTextAsync("queued later", behavior: UserTextBehavior.Queue));
        hold.TrySetResult();
        var firstCompleted = await output.WaitForAsync(item => item.Payload is SpeechOutputCompletedOutput);
        Assert.True(await runtime.SubmitPlaybackAsync(firstCompleted.ResponseId!.Value, "stopped", 0, 0));
        Assert.Equal(ResponseCancelResult.Cancelled, await runtime.CancelResponseAsync(firstCompleted.ResponseId!.Value));
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(1, runtime.Snapshot.Entries.Count(entry => entry.Role == ConversationRole.User && entry.Text == "queued later"));
        Assert.Equal(1, runtime.Snapshot.Entries.Count(entry => entry.Role == ConversationRole.Assistant));

        var output2 = new CapturingSessionOutput();
        var hold2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var success = Create(output2, new ScriptedLanguageModel(["Hello from ", "synthetic."], hold2));
        await success.AttachAsync();
        await success.SetModeAsync(SessionMode.Voice);
        await output2.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        await success.SubmitUserTextAsync("Hello");
        await output2.WaitForAsync(item => item.Payload is TextDeltaOutput);
        Assert.True(await success.SubmitUserTextAsync("queued later", behavior: UserTextBehavior.Queue));
        hold2.TrySetResult();
        var done = await output2.WaitForAsync(item => item.Payload is SpeechOutputCompletedOutput);
        await AckClientSpeechAsync(success, done);
        var second = await output2.WaitForAsync(
            item => item.Payload is SpeechOutputCompletedOutput && item.ResponseId != done.ResponseId);
        await AckClientSpeechAsync(success, second);
        await success.WaitUntilIdleAsync();
        Assert.Equal(2, success.Snapshot.Entries.Count(entry => entry.Role == ConversationRole.Assistant));
    }

    private static SessionRuntime Create(ISessionOutput output, ILanguageModel model)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 64).Select(index => Guid.Parse($"019944af-00c0-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940c000")]);
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
            voice: new VoiceAvailability
            {
                SpeechAdaptersResolved = true,
                Plan = new EffectiveSpeechPlan(
                    SpeechTransport.ServerAudio,
                    SpeechTransport.ClientSpeech,
                    true,
                    true,
                    null,
                    null)
            });
    }

    private static async Task AckClientSpeechAsync(SessionRuntime runtime, SessionOutput completed)
    {
        var payload = Assert.IsType<SpeechOutputCompletedOutput>(completed.Payload);
        var responseId = completed.ResponseId!.Value;
        Assert.True(await runtime.SubmitPlaybackAsync(responseId, "started", 0, 0));
        Assert.True(await runtime.SubmitPlaybackAsync(responseId, "progress", 0, payload.TextEndExclusive));
        Assert.True(await runtime.SubmitPlaybackAsync(responseId, "completed", 0, payload.TextEndExclusive));
    }
}
