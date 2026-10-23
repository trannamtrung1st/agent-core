using System.Runtime.CompilerServices;
using AgentCore.Application.Execution;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.SemanticResponses;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed partial class AgentRunDurabilityTests
{
    // The transcript contains an already completed read. Recovery must use its receipt,
    // never offer another fetch or change Run/Response identity.
    private static readonly ModelMessage[] VerifiedRead = [
        new(ModelRole.Assistant, "", ToolCalls: [new("read", "web.fetch", "{\"url\":\"https://example.test/\"}")]),
        new(ModelRole.Tool, "{\"status\":\"ok\",\"text\":\"Verified source finding\"}", ToolCallId: "read", Name: "web.fetch")];

    [Theory]
    [InlineData(ProviderErrorCode.Authentication, ProviderFailureReason.TransportFailure)]
    [InlineData(ProviderErrorCode.Timeout, ProviderFailureReason.TotalTimeout)]
    [InlineData(ProviderErrorCode.InvalidRequest, ProviderFailureReason.TransportFailure)]
    public async Task Unrelated_provider_failure_keeps_attempt_details_without_response_recovery_panel(ProviderErrorCode code, string reason)
    {
        var (snapshot, run, runs, memory, time) = await BackgroundRecoveryFixture();
        var model = new BackgroundRecoveryModel(true, failure: new(code, "Provider request failed.", FailureReason: reason));
        await using var runtime = CreateRuntime(new CapturingSessionOutput(), model, time, runs, memory, snapshot);
        Assert.True(await runtime.DispatchAgentRunAsync(run.AgentRunId, headless: true));
        await runtime.WaitUntilIdleAsync();
        var terminal = (await runs.GetAsync(run.Owner, run.AgentRunId))!;
        Assert.Equal(AgentRunStatus.Failed, terminal.Status);
        Assert.Single(model.Requests);
        // Historical checkpoint data remains intact; only the public read projection changes.
        Assert.NotNull(AgentRunToolCallCheckpoint.ReadExecutionState(terminal.Checkpoint).ProtocolRecovery);
        var diagnostic = RunDiagnosticProjection.From(terminal)!;
        Assert.Null(diagnostic.ProtocolRecovery);
        Assert.Equal(reason, Assert.Single(diagnostic.FailedAttempts).Reason);
    }

    [Theory]
    [InlineData(ProviderResponseChannel.StructuredOutput, "native structured output")]
    [InlineData(ProviderResponseChannel.ResponseFunction, "response function")]
    [InlineData(ProviderResponseChannel.MarkerCompatibility, "marker-compatible response")]
    public async Task Terminal_semantic_summary_names_the_actual_response_channel(string channel, string label)
    {
        var (snapshot, run, runs, memory, time) = await BackgroundRecoveryFixture();
        var model = new BackgroundRecoveryModel(true, failure: new(ProviderErrorCode.InvalidResponse, "Malformed response.",
            FailureReason: ProviderFailureReason.InvalidBlocks, ResponseChannel: channel), exhaust: true);
        await using var runtime = CreateRuntime(new CapturingSessionOutput(), model, time, runs, memory, snapshot);
        Assert.True(await runtime.DispatchAgentRunAsync(run.AgentRunId, headless: true));
        await runtime.WaitUntilIdleAsync();
        var terminal = (await runs.GetAsync(run.Owner, run.AgentRunId))!;
        Assert.Equal(AgentRunStatus.Failed, terminal.Status);
        Assert.Equal("semantic-response-invalid", terminal.Failure!.Code);
        Assert.Contains("through " + label + ".", terminal.Failure.Summary);
        Assert.Equal(channel, RunDiagnosticProjection.From(terminal)!.ProtocolRecovery!.Channel);
        Assert.Equal(2, model.Requests.Count);
    }

    [Theory]
    [InlineData(ProviderErrorCode.Cancelled, ProviderFailureReason.InvalidJson, ProviderResponseChannel.StructuredOutput)]
    [InlineData(ProviderErrorCode.Authentication, ProviderFailureReason.InvalidJson, ProviderResponseChannel.StructuredOutput)]
    [InlineData(ProviderErrorCode.InvalidResponse, ProviderFailureReason.ModelSuppliedDestination, ProviderResponseChannel.StructuredOutput)]
    [InlineData(ProviderErrorCode.InvalidResponse, ProviderFailureReason.UnknownAction, ProviderResponseChannel.ResponseFunction)]
    [InlineData(ProviderErrorCode.InvalidResponse, ProviderFailureReason.InvalidMemory, ProviderResponseChannel.ResponseFunction)]
    public async Task Unsafe_background_failure_remains_terminal(ProviderErrorCode code, string reason, string channel)
    {
        var (snapshot, run, runs, memory, time) = await BackgroundRecoveryFixture();
        var model = new BackgroundRecoveryModel(true, failure: new(code, "Rejected response.", FailureReason: reason, ResponseChannel: channel));
        await using var runtime = CreateRuntime(new CapturingSessionOutput(), model, time, runs, memory, snapshot);
        Assert.True(await runtime.DispatchAgentRunAsync(run.AgentRunId, headless: true));
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(AgentRunStatus.Failed, (await runs.GetAsync(run.Owner, run.AgentRunId))!.Status);
        Assert.Single(model.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Uncertain_effect_blocks_background_semantic_recovery(bool indeterminate)
    {
        var (snapshot, run, runs, memory, time) = await BackgroundRecoveryFixture();
        foreach (var disposition in indeterminate
            ? new[] { AgentRunSideEffectDisposition.Prepared, AgentRunSideEffectDisposition.InFlight, AgentRunSideEffectDisposition.Indeterminate }
            : new[] { AgentRunSideEffectDisposition.Prepared, AgentRunSideEffectDisposition.InFlight })
            run = await runs.ApplyAsync(run.Owner, run.AgentRunId, new AgentRunCommand.MarkSideEffect(run.Revision, time.GetUtcNow(),
                run.Claim!.Generation, disposition, "uncertain-action", new string('a', 64)));
        var model = new BackgroundRecoveryModel(true, failure: new(ProviderErrorCode.InvalidResponse, "Malformed response.",
            FailureReason: ProviderFailureReason.InvalidJson, ResponseChannel: ProviderResponseChannel.StructuredOutput));
        await using var runtime = CreateRuntime(new CapturingSessionOutput(), model, time, runs, memory, snapshot);
        Assert.True(await runtime.DispatchAgentRunAsync(run.AgentRunId, headless: true));
        await runtime.WaitUntilIdleAsync();
        var terminal = (await runs.GetAsync(run.Owner, run.AgentRunId))!;
        Assert.Equal(AgentRunStatus.Failed, terminal.Status);
        Assert.Single(model.Requests);
        Assert.Equal("uncertainEffect", AgentRunToolCallCheckpoint.ReadExecutionState(terminal.Checkpoint).ProtocolRecovery!.Eligibility);
        Assert.Null(terminal.Result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_or_deadline_during_background_correction_does_not_generate_again(bool deadline)
    {
        var (snapshot, run, runs, memory, time) = await BackgroundRecoveryFixture();
        var model = new HoldingBackgroundCorrection();
        await using var runtime = CreateRuntime(new CapturingSessionOutput(), model, time, runs, memory, snapshot);
        Assert.True(await runtime.DispatchAgentRunAsync(run.AgentRunId, headless: true));
        await model.Correcting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (deadline) time.Advance(TimeSpan.FromSeconds(31));
        else Assert.Equal(ResponseCancelResult.Cancelled, await runtime.CancelResponseAsync(run.ResponseId!.Value));
        await runtime.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var terminal = (await runs.GetAsync(run.Owner, run.AgentRunId))!;
        Assert.Equal(deadline ? AgentRunStatus.Failed : AgentRunStatus.Cancelled, terminal.Status);
        Assert.Equal(2, model.Calls);
        Assert.Null(terminal.Result);
        Assert.Equal(deadline ? "failed" : "cancelled", RunDiagnosticProjection.From(terminal)!.ProtocolRecovery!.Outcome);
    }

    private sealed class HoldingBackgroundCorrection : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: true);
        public TaskCompletionSource Correcting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (++Calls == 1)
                yield return new ModelFailed(new(ProviderErrorCode.InvalidResponse, "Invalid blocks.",
                    FailureReason: ProviderFailureReason.InvalidBlocks, ResponseChannel: ProviderResponseChannel.ResponseFunction));
            else { Correcting.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
        }
    }

    [Fact]
    public async Task Restart_after_reserved_recovery_does_not_reset_allowance_or_replay_read()
    {
        var recovery = new RunProtocolRecovery(ProviderFailureReason.InvalidBlocks, ProviderResponseChannel.ResponseFunction,
            "eligible", "completionOnly", "started");
        var (snapshot, run, runs, memory, time) = await BackgroundRecoveryFixture(recovery);
        var model = new BackgroundRecoveryModel(true);
        // A fresh runtime reads only the serialized checkpoint, as after process loss.
        await using var runtime = CreateRuntime(new CapturingSessionOutput(), model, time, runs, memory, (await memory.LoadAsync(snapshot.SessionId))!);
        Assert.True(await runtime.DispatchAgentRunAsync(run.AgentRunId, headless: true));
        await runtime.WaitUntilIdleAsync();
        var terminal = (await runs.GetAsync(run.Owner, run.AgentRunId))!;
        Assert.Equal(AgentRunStatus.Failed, terminal.Status);
        Assert.Empty(model.Requests);
        Assert.Equal("interrupted", AgentRunToolCallCheckpoint.ReadExecutionState(terminal.Checkpoint).ProtocolRecovery!.Outcome);
        Assert.True(AgentRunToolCallCheckpoint.TryRead(terminal.Checkpoint, out var messages));
        Assert.Single(messages!, m => m.Name == "web.fetch");
    }

    private static async Task<(SessionSnapshot, AgentRun, RuntimeAgentRunStore, InMemoryMemoryStore, FakeTimeProvider)> BackgroundRecoveryFixture(RunProtocolRecovery? recovery = null)
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-16T00:00:00Z"));
        var memory = new InMemoryMemoryStore(); var runs = new RuntimeAgentRunStore(); runs.Bind(memory);
        var now = time.GetUtcNow(); var runId = Guid.NewGuid(); var sessionId = Guid.NewGuid();
        var input = new ConversationEntry(Guid.NewGuid(), 1, Guid.NewGuid(), ConversationRole.User, "Report verified research", null,
            EntryStatus.Completed, SessionMode.Text, 0, 24, now);
        var snapshot = RuntimeAgentRunStore.WithPins(new SessionSnapshot(1, sessionId, 1, SampleDefinitions.Examiner, SessionMode.Text,
            null, SessionStatus.Created, [input], "", 0, null, null, now, now, AgentInstanceId: Guid.NewGuid(),
            ModelSelection: new("synthetic-offline/scripted", "primary-llm", "scripted", ModelSelectionSource.Host, null),
            Origin: new(SessionOriginKind.ManualBackground, initialBackgroundAgentRunId: runId), Surfaces: SessionSurface.BackgroundWork));
        var activation = Activation.Start(Guid.NewGuid(), sessionId, ActivationKind.ManualBackground, [input.EntryId], input.SourceEventId,
            null, null, null, "manual:semantic", now);
        var run = AgentRun.Create(runId, new(snapshot.AgentInstanceId, snapshot.ProfileId!.Value),
            new(activation, snapshot.Definition.Id, snapshot.Definition.Version, snapshot.PinnedPersona!, Guid.NewGuid(), AgentRunOutputContract.BackgroundOutcome,
                configuration: new(snapshot.Definition, 0, snapshot.PinnedPersonaRevision ?? 0, [])),
            new("synthetic-offline/scripted", "primary-llm", "scripted", null), 3, now);
        await runs.AdmitAsync(snapshot, 0, run);
        run = await runs.ApplyAsync(run.Owner, runId, new AgentRunCommand.Claim(run.Revision, now, Guid.NewGuid(), now.AddMinutes(5)));
        var state = new ToolLoopExecutionState(recovery is null ? RunPhase.Work : RunPhase.Finalization,
            recovery is null ? null : "protocolRepair", TranscriptId: run.ResponseId, ProtocolRecovery: recovery);
        run = await runs.ApplyAsync(run.Owner, runId, new AgentRunCommand.Checkpoint(run.Revision, now, run.Claim!.Generation,
            new(AgentRunToolCallCheckpoint.Write(VerifiedRead, protocolRepairReason: recovery?.Reason, executionState: state), 1, 64, 120000), null));
        return (snapshot, run, runs, memory, time);
    }

    private sealed class BackgroundRecoveryModel(bool native, ProviderFailure? failure = null, bool exhaust = false, bool plainThenComplete = false, bool invalidCompletion = false) : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: true, StructuredOutput: native);
        public List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            await Task.Yield(); ct.ThrowIfCancellationRequested();
            if (failure is not null && (Requests.Count == 1 || exhaust)) { yield return new ModelFailed(failure); yield break; }
            if (plainThenComplete && Requests.Count == 1)
            {
                yield return new ModelTextDelta("A malformed conversational envelope is irrelevant to background completion.");
                yield return new ModelCompleted(ModelStopReason.Completed); yield break;
            }
            yield return new ModelToolCallEvent(new("finish", ToolCatalog.WorkComplete,
                invalidCompletion ? "{\"summary\":\"Invalid outcome\",\"attentionRequired\":true,\"outcome\":\"Response\"}"
                    : "{\"summary\":\"One source was verified; remaining research is incomplete.\",\"attentionRequired\":true,\"outcome\":\"NeedsAttention\"}"));
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
        }
    }
}
