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
    [InlineData(true)]
    [InlineData(false)]
    public async Task Background_work_has_no_conversational_contract_on_either_transport(bool native)
    {
        var inner = new BackgroundRecoveryModel(native, plainThenComplete: true);
        var (snapshot, run, runs, memory, time) = await BackgroundRecoveryFixture();
        await using var runtime = CreateRuntime(new CapturingSessionOutput(), new SemanticResponseLanguageModel(inner), time, runs, memory, snapshot);
        Assert.True(await runtime.DispatchAgentRunAsync(run.AgentRunId, headless: true));
        await runtime.WaitUntilIdleAsync();
        var terminal = (await runs.GetAsync(run.Owner, run.AgentRunId))!;
        Assert.Equal(AgentRunStatus.Completed, terminal.Status);
        Assert.Equal(2, inner.Requests.Count);
        Assert.All(inner.Requests, r => {
            Assert.Null(r.ResponseContract);
            Assert.DoesNotContain(r.Tools ?? [], t => t.Name == AssistantResponseSchema.ResponseFunctionName);
            Assert.DoesNotContain(r.Messages, m => m.Text.Contains("required response protocol", StringComparison.Ordinal));
        });
        Assert.Equal(ToolCatalog.WorkComplete, Assert.Single(inner.Requests[1].Tools!).Name);
    }

    [Theory]
    [InlineData(ProviderFailureReason.InvalidBlocks, ProviderResponseChannel.ResponseFunction, false)]
    [InlineData(ProviderFailureReason.InvalidJson, ProviderResponseChannel.StructuredOutput, false)]
    [InlineData(ProviderFailureReason.ResponseFunctionArgumentsInvalid, ProviderResponseChannel.ResponseFunction, false)]
    [InlineData(ProviderFailureReason.InvalidMarkerEnvelope, ProviderResponseChannel.MarkerCompatibility, false)]
    [InlineData(ProviderFailureReason.OutputLimit, ProviderResponseChannel.StructuredOutput, false)]
    [InlineData(ProviderFailureReason.InvalidBlocks, ProviderResponseChannel.ResponseFunction, true)]
    public async Task Background_format_failure_uses_one_durable_completion_only_recovery(string reason, string channel, bool exhaust)
    {
        var (snapshot, run, runs, memory, time) = await BackgroundRecoveryFixture();
        var model = new BackgroundRecoveryModel(true, failure: new(ProviderErrorCode.InvalidResponse, "Malformed response.", FailureReason: reason, ResponseChannel: channel), exhaust: exhaust);
        await using var runtime = CreateRuntime(new CapturingSessionOutput(), model, time, runs, memory, snapshot);
        Assert.True(await runtime.DispatchAgentRunAsync(run.AgentRunId, headless: true));
        await runtime.WaitUntilIdleAsync();
        var terminal = (await runs.GetAsync(run.Owner, run.AgentRunId))!;
        Assert.Equal(exhaust ? AgentRunStatus.Failed : AgentRunStatus.Completed, terminal.Status);
        Assert.Equal(run.ResponseId, terminal.ResponseId);
        Assert.Equal(1, terminal.AttemptCount);
        Assert.Equal(2, model.Requests.Count);
        var correction = model.Requests[1];
        Assert.Equal(ToolCatalog.WorkComplete, Assert.Single(correction.Tools!).Name);
        Assert.Equal(ModelToolChoice.Named, correction.ToolChoice);
        Assert.Null(correction.ResponseContract);
        Assert.Single(correction.Interaction!, m => m.Role == ModelRole.Tool && m.Name == "web.fetch");
        var state = AgentRunToolCallCheckpoint.ReadExecutionState(terminal.Checkpoint);
        Assert.Equal("completionOnly", state.ProtocolRecovery!.Strategy);
        Assert.Equal(exhaust ? "failed" : "succeeded", state.ProtocolRecovery.Outcome);
        Assert.Equal(reason, state.FailedAttempts![0].Reason);
        if (!exhaust) Assert.Equal(AgentRunOutcomeKind.NeedsAttention, terminal.Result!.OutcomeKind);
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

    [Fact]
    public async Task Semantic_correction_cannot_obtain_a_second_generation_through_completion_validation()
    {
        var (snapshot, run, runs, memory, time) = await BackgroundRecoveryFixture();
        var model = new BackgroundRecoveryModel(true, failure: new(ProviderErrorCode.InvalidResponse, "Invalid blocks.",
            FailureReason: ProviderFailureReason.InvalidBlocks, ResponseChannel: ProviderResponseChannel.ResponseFunction), invalidCompletion: true);
        await using var runtime = CreateRuntime(new CapturingSessionOutput(), model, time, runs, memory, snapshot);
        Assert.True(await runtime.DispatchAgentRunAsync(run.AgentRunId, headless: true));
        await runtime.WaitUntilIdleAsync();
        var terminal = (await runs.GetAsync(run.Owner, run.AgentRunId))!;
        Assert.Equal(AgentRunStatus.Failed, terminal.Status);
        Assert.Equal("completion-invalid", terminal.Failure!.Code);
        Assert.Equal(2, model.Requests.Count);
        Assert.Equal("failed", AgentRunToolCallCheckpoint.ReadExecutionState(terminal.Checkpoint).ProtocolRecovery!.Outcome);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restart_continues_only_known_transport_retry_or_saved_completion(bool completionSaved)
    {
        var recovery = new RunProtocolRecovery(ProviderFailureReason.InvalidBlocks, ProviderResponseChannel.ResponseFunction,
            "eligible", "completionOnly", completionSaved ? "started" : "retryPending");
        var (snapshot, run, runs, memory, time) = await BackgroundRecoveryFixture(recovery);
        if (completionSaved)
        {
            var messages = VerifiedRead.Append(new ModelMessage(ModelRole.Assistant, "", ToolCalls: [new("saved-completion", ToolCatalog.WorkComplete,
                "{\"summary\":\"One source verified; more work remains.\",\"attentionRequired\":true,\"outcome\":\"NeedsAttention\"}")])).ToArray();
            run = await runs.ApplyAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Checkpoint(run.Revision, time.GetUtcNow(), run.Claim!.Generation,
                new(AgentRunToolCallCheckpoint.Write(messages, protocolRepairReason: recovery.Reason,
                    executionState: new(RunPhase.Finalization, "protocolRepair", TranscriptId: run.ResponseId, ProtocolRecovery: recovery)), 2, 64, 25000), null));
        }
        var model = new BackgroundRecoveryModel(true);
        await using var runtime = CreateRuntime(new CapturingSessionOutput(), model, time, runs, memory, (await memory.LoadAsync(snapshot.SessionId))!);
        Assert.True(await runtime.DispatchAgentRunAsync(run.AgentRunId, headless: true));
        await runtime.WaitUntilIdleAsync();
        var terminal = (await runs.GetAsync(run.Owner, run.AgentRunId))!;
        Assert.Equal(AgentRunStatus.Completed, terminal.Status);
        Assert.Equal(AgentRunOutcomeKind.NeedsAttention, terminal.Result!.OutcomeKind);
        Assert.Equal(completionSaved ? 0 : 1, model.Requests.Count);
        Assert.All(model.Requests, r => Assert.Equal(ToolCatalog.WorkComplete, Assert.Single(r.Tools!).Name));
        Assert.Equal("succeeded", AgentRunToolCallCheckpoint.ReadExecutionState(terminal.Checkpoint).ProtocolRecovery!.Outcome);
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
        var activation = new Activation(Guid.NewGuid(), sessionId, ActivationKind.ManualBackground, [input.EntryId], input.SourceEventId,
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
