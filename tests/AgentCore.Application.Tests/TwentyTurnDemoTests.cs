using System.Diagnostics;
using AgentCore.Application.Agents;
using AgentCore.Application.Events;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class TwentyTurnDemoTests
{
    [Theory]
    [InlineData("examiner")]
    [InlineData("customer-support")]
    public async Task Twenty_turn_synthetic_demo_records_observed_stage_latencies(string agentId)
    {
        RuntimeTelemetry.Reset();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        var output = new CapturingSessionOutput();
        var store = new InMemoryMemoryStore();
        var definition = agentId == "examiner" ? SampleDefinitions.Examiner : SampleDefinitions.Support;
        await using var runtime = Create(output, store, time, definition, new SyntheticSpeechRecognizer(["Wait stop"]), new SyntheticSpeechSynthesizer());
        await runtime.AttachAsync();
        await output.WaitForAsync(item => item.Payload is ReadyOutput);

        var turnDurations = new List<double>(20);
        for (var turn = 1; turn <= 20; turn++)
        {
            var started = Stopwatch.GetTimestamp();
            await runtime.SubmitUserTextAsync($"Turn {turn} hello");
            await runtime.WaitUntilIdleAsync();
            turnDurations.Add(RuntimeTelemetry.ElapsedMs(started));
        }

        var interruptOutput = new CapturingSessionOutput();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var interrupting = Create(
            interruptOutput,
            new InMemoryMemoryStore(),
            time,
            definition,
            new SyntheticSpeechRecognizer(["Wait stop"]),
            new SyntheticSpeechSynthesizer(),
            new ScriptedLanguageModel(ScriptedLanguageModel.LongerChunks, gate));
        await interrupting.AttachAsync();
        await interrupting.SubmitUserTextAsync("Please explain");
        await interruptOutput.WaitForAsync(item => item.Payload is TextDeltaOutput);
        await interrupting.SubmitUserTextAsync("Wait, stop");
        gate.TrySetResult();
        await interrupting.WaitUntilIdleAsync();
        Assert.Contains(
            interruptOutput.Items,
            item => item.Payload is ResponseCompletedOutput completed && completed.InterruptReason == "newText");

        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        await runtime.SubmitUserTextAsync("Hello");
        time.Advance(TimeSpan.FromSeconds(1));
        var audio = await output.WaitForAsync(item => item.Payload is AudioFrameOutput);
        Assert.True(runtime.RecognitionActive);
        var utterance = Guid.Parse("019944af-0000-7000-8000-0000000000d9");
        Assert.True(runtime.TryAdmitAudio(new AudioFrame(1, 0, new byte[960])));
        Assert.True(runtime.TryAdmitBoundary(utterance, SpeechBoundary.Started, 0.9));
        Assert.True(runtime.TryAdmitBoundary(utterance, SpeechBoundary.Ended, 0.2));
        await output.WaitForAsync(item => item.Payload is TranscriptFinalOutput);
        await runtime.SubmitPlaybackAsync(audio.ResponseId!.Value, "started", 0, 0);
        await runtime.SubmitPlaybackAsync(runtime.ActiveResponseId ?? audio.ResponseId.Value, "completed", runtime.SentSamples, 0);
        await runtime.WaitUntilIdleAsync();

        if (agentId == "examiner")
        {
            await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
            await runtime.WaitUntilIdleAsync();
        }
        else
        {
            await runtime.SubmitEnvironmentAsync(
                SyntheticEnvironmentDriver.OrderShipped(Guid.Parse("019944af-0000-7000-8000-0000000000ea"), "D-9"));
            await runtime.WaitUntilIdleAsync();
        }

        await runtime.DetachAsync();
        await runtime.WaitUntilMailboxDrainedAsync();
        await runtime.AttachAsync();
        await output.WaitForAsync(item => item.Payload is ReadyOutput);
        await runtime.WaitUntilIdleAsync();

        var snap = runtime.Snapshot;
        var lastAssistant = snap.Entries.Last(entry => entry.Role == ConversationRole.Assistant);
        Assert.True(lastAssistant.Text.Length >= lastAssistant.ReceivedTextEndExclusive);
        Assert.True(lastAssistant.ReceivedTextEndExclusive >= lastAssistant.HeardTextEndExclusive);
        Assert.Equal(20, turnDurations.Count);
        Assert.All(turnDurations, duration => Assert.True(duration >= 0));
        var stats = RuntimeTelemetry.SnapshotStats();
        Assert.True(stats.ContainsKey("controller"));
        Assert.True(stats.ContainsKey("llm") || stats.ContainsKey("persist"));
        Assert.True(turnDurations.Average() < 5_000);
    }

    private static SessionRuntime Create(
        ISessionOutput output,
        IMemoryStore store,
        FakeTimeProvider time,
        AgentDefinition definition,
        ISpeechRecognizer recognizer,
        ISpeechSynthesizer synthesizer,
        ILanguageModel? model = null)
    {
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 2000).Select(index => Guid.Parse($"019944af-0001-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b850"), Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b851")]);
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
            model ?? new ScriptedLanguageModel(),
            new DefaultAgentBrain(new PromptContextBuilder()),
            store,
            output,
            ids,
            time,
            NullLogger<SessionRuntime>.Instance,
            recognizer: recognizer,
            synthesizer: synthesizer);
    }
}
