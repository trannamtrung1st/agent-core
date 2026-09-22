using System.Diagnostics;
using System.Globalization;
using System.Text;
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

[Collection("telemetry-global")]
public sealed class TwentyTurnDemoTests
{
    private static readonly string[] RequiredStages =
        ["controller", "llm", "persist", "stt", "segmentation", "tts", "transport", "playback"];

    [Fact]
    public async Task Twenty_turn_synthetic_demo_records_observed_stage_latencies()
    {
        RuntimeTelemetry.Reset();
        var baseline = RuntimeTelemetry.SnapshotStats();
        foreach (var agentId in new[] { "examiner", "customer-support" })
        {
            await RunIdentityAsync(agentId);
        }

        var stats = RuntimeTelemetry.SnapshotStats();
        foreach (var stage in RequiredStages)
        {
            Assert.True(stats.ContainsKey(stage), $"missing stage {stage}");
            var baselineCount = baseline.TryGetValue(stage, out var prior) ? prior.Count : 0;
            Assert.True(stats[stage].Count > baselineCount, $"stage {stage} did not record new samples");
            Assert.True(stats[stage].MaxMs >= 0);
            Assert.True(stats[stage].MaxMs < 60_000);
            Assert.True(stats[stage].P50Ms >= 0);
            Assert.True(stats[stage].P50Ms <= stats[stage].MaxMs);
            Assert.True(stats[stage].P95Ms >= 0);
            Assert.True(stats[stage].P95Ms <= stats[stage].MaxMs);
        }

        if (string.Equals(Environment.GetEnvironmentVariable("AGENTCORE_WRITE_STAGE_LATENCIES"), "1", StringComparison.Ordinal))
        {
            WriteStageTable(stats);
        }
    }

    private static async Task RunIdentityAsync(string agentId)
    {
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
        await interruptOutput.WaitForAsync(item => item.Payload is ReadyOutput);
        await interrupting.SubmitUserTextAsync("Please explain");
        await interruptOutput.WaitForAsync(item => item.Payload is TextDeltaOutput);
        await interrupting.SubmitUserTextAsync("Wait, stop");
        await interruptOutput.WaitForAsync(
            item => item.Payload is ResponseCompletedOutput completed && completed.InterruptReason == "userSteer");
        gate.TrySetResult();
        await interrupting.WaitUntilIdleAsync();

        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        await runtime.SubmitUserTextAsync("Hello");
        time.Advance(TimeSpan.FromSeconds(1));
        var audio = await output.WaitForAsync(item =>
            item.Payload is AudioFrameOutput frame && !frame.IsFinal && item.ResponseId is not null);
        Assert.True(runtime.RecognitionActive);
        var responseId = runtime.ActiveResponseId ?? audio.ResponseId!.Value;
        var generated = runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant).Text.Length;
        Assert.True(await runtime.SubmitPlaybackAsync(responseId, "started", 0, generated) is true);
        Assert.True(await runtime.SubmitPlaybackAsync(responseId, "completed", runtime.SentSamples, generated) is true);
        await runtime.WaitUntilIdleAsync();
        var utterance = Guid.Parse("019944af-0000-7000-8000-0000000000d9");
        Assert.True(runtime.TryAdmitAudio(new AudioFrame(1, 0, new byte[960])));
        Assert.True(runtime.TryAdmitBoundary(utterance, SpeechBoundary.Started, 0.9));
        Assert.True(runtime.TryAdmitBoundary(utterance, SpeechBoundary.Ended, 0.2));
        await output.WaitForAsync(item => item.Payload is TranscriptFinalOutput);

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

        var initiativeAssistant = runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant);
        if (initiativeAssistant.ResponseId is { } initiativeResponseId
            && initiativeAssistant.ReceivedTextEndExclusive < initiativeAssistant.Text.Length)
        {
            Assert.True(await runtime.SubmitReceiptAsync(initiativeResponseId, initiativeAssistant.Text.Length));
            await runtime.WaitUntilMailboxDrainedAsync();
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
        Assert.True(turnDurations.Max() < 60_000);
    }

    private static void WriteStageTable(IReadOnlyDictionary<string, StageStats> stats)
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "docs", "reports", "m12-stage-latencies.md");
        var builder = new StringBuilder();
        builder.AppendLine("# Observed per-stage latencies (20-turn synthetic demo)");
        builder.AppendLine();
        builder.AppendLine("Generated by an isolated `TwentyTurnDemoTests` run (`AGENTCORE_WRITE_STAGE_LATENCIES=1`) from `Stopwatch` samples on `AgentCore.Runtime`. Synthetic/in-process; not SLAs. The default full suite does not rewrite this file.");
        builder.AppendLine("Browser/device: N/A (in-process FakeTimeProvider; no browser or audio device).");
        builder.AppendLine();
        builder.AppendLine("| Stage | Count | p50 (ms) | p95 (ms) | max (ms) |");
        builder.AppendLine("| --- | ---: | ---: | ---: | ---: |");
        foreach (var stage in RequiredStages.Concat(stats.Keys.Except(RequiredStages, StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal)))
        {
            if (!stats.TryGetValue(stage, out var sample))
            {
                continue;
            }

            builder.AppendLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"| `{stage}` | {sample.Count} | {sample.P50Ms:F3} | {sample.P95Ms:F3} | {sample.MaxMs:F3} |"));
        }

        File.WriteAllText(path, builder.ToString());
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AgentCore.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException();
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
