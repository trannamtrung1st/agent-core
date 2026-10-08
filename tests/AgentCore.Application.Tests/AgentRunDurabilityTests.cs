using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Events;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class AgentRunDurabilityTests
{
    [Fact]
    public async Task Coordinator_winning_fast_admission_claim_launches_exactly_once()
    {
        var runs = new RuntimeAgentRunStore();
        var output = new CapturingSessionOutput();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero));
        var model = new CountingLanguageModel();
        AgentRun? winner = null;
        runs.BeforeApply = async (owner, id, command, ct) =>
        {
            if (command is not AgentCore.Application.Execution.AgentRunCommand.Claim) return;
            runs.BeforeApply = null;
            var queued = (await runs.GetAsync(owner, id, ct))!;
            winner = await runs.ApplyAsync(owner, id,
                new AgentCore.Application.Execution.AgentRunCommand.Claim(queued.Revision, time.GetUtcNow(), Guid.NewGuid(), time.GetUtcNow().AddMinutes(5)), ct);
        };
        await using var runtime = CreateRuntime(output, model, time, runs);
        await runtime.AttachAsync();
        Assert.True(await runtime.SubmitPersistedUserTextAsync("Check inventory", Guid.NewGuid()));
        await runtime.WaitUntilIdleAsync();
        Assert.NotNull(winner);
        Assert.Equal(0, model.Requests);
        Assert.Equal(1, winner.AttemptCount);
        Assert.True(await runtime.DispatchAgentRunAsync(winner.AgentRunId, false));
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(1, model.Requests);
        var completed = (await runs.GetAsync(winner.Owner, winner.AgentRunId))!;
        Assert.Equal(AgentRunStatus.Completed, completed.Status);
        Assert.Equal(1, completed.AttemptCount);
        Assert.Equal(winner.ResponseId, completed.ResponseId);
        Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant);
    }

    [Fact]
    public async Task Fast_user_run_rechecks_current_authority_before_any_provider_request()
    {
        var output = new CapturingSessionOutput();
        var runs = new RuntimeAgentRunStore();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero));
        var model = new CountingLanguageModel();
        await using var runtime = CreateRuntime(output, model, time, runs, authority: new DeniedAuthority());
        await runtime.AttachAsync();
        var eventId = Guid.NewGuid();
        Assert.True(await runtime.SubmitPersistedUserTextAsync("Check the current inventory", eventId));
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(0, model.Requests);
        var run = (await runs.ForSourceAsync(runtime.SessionId, eventId))!;
        Assert.Equal(AgentRunStatus.Failed, run.Status);
        Assert.Equal(EntryStatus.Failed, Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant).Status);
        Assert.Empty(await runs.OpenAsync(runtime.SessionId));
    }

    [Fact]
    public async Task User_turn_creates_durable_execution_and_completes_with_assistant()
    {
        var output = new CapturingSessionOutput();
        var store = new RuntimeAgentRunStore();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero));
        await using var runtime = CreateRuntime(output, new ScriptedLanguageModel(), time, store);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("hi");
        await runtime.WaitUntilIdleAsync();

        var open = await store.OpenAsync(runtime.SessionId);
        Assert.Empty(open);

        var assistant = runtime.Snapshot.Entries.Single(e => e.Role == ConversationRole.Assistant);
        Assert.Equal(EntryStatus.Completed, assistant.Status);
        Assert.False(string.IsNullOrWhiteSpace(assistant.Text));
    }

    [Fact]
    public async Task Reload_after_runtime_disposal_shows_persisted_assistant()
    {
        var memory = new InMemoryMemoryStore();
        var turns = new RuntimeAgentRunStore();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero));
        Guid sessionId;
        await using (var runtime = CreateRuntime(new CapturingSessionOutput(), new ScriptedLanguageModel(), time, turns, memory))
        {
            sessionId = runtime.SessionId;
            await runtime.AttachAsync();
            await runtime.SubmitUserTextAsync("hi");
            await runtime.WaitUntilIdleAsync();
        }

        var durable = (await memory.LoadAsync(sessionId))!;
        await using var restored = CreateRuntime(
            new CapturingSessionOutput(),
            new ScriptedLanguageModel(),
            time,
            turns,
            memory,
            durable);
        var assistant = restored.Snapshot.Entries.Single(entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal(EntryStatus.Completed, assistant.Status);
        Assert.False(string.IsNullOrWhiteSpace(assistant.Text));
        Assert.Empty(await turns.OpenAsync(sessionId));
        Assert.DoesNotContain(
            restored.Snapshot.Entries,
            entry => entry.Role == ConversationRole.Assistant && string.IsNullOrWhiteSpace(entry.Text) && entry.Status != EntryStatus.Streaming);
    }

    [Fact]
    public async Task Repeated_source_event_does_not_duplicate_turn_or_execution()
    {
        var turns = new RuntimeAgentRunStore();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero));
        await using var runtime = CreateRuntime(new CapturingSessionOutput(), new ScriptedLanguageModel(), time, turns);
        await runtime.AttachAsync();
        var eventId = Guid.Parse("019944af-0000-7000-8000-000000000099");
        Assert.True(await runtime.SubmitPersistedUserTextAsync("hi", eventId));
        await runtime.WaitUntilIdleAsync();
        Assert.True(await runtime.SubmitPersistedUserTextAsync("hi", eventId));
        await runtime.WaitUntilIdleAsync();

        Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.User);
        Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant);
        Assert.Empty(await turns.OpenAsync(runtime.SessionId));
    }

    [Fact]
    public async Task Queued_turn_survives_detach_and_keeps_order()
    {
        var turns = new RuntimeAgentRunStore();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero));
        var model = new HoldingLanguageModel();
        await using var runtime = CreateRuntime(new CapturingSessionOutput(), model, time, turns);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("first");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (runtime.ActiveResponseId is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        var queuedEventId = Guid.Parse("019944af-0000-7000-8000-000000000098");
        Assert.True(await runtime.SubmitPersistedUserTextAsync(
            "second",
            queuedEventId,
            behavior: UserTextBehavior.Queue));
        var thirdEventId = Guid.Parse("019944af-0000-7000-8000-000000000097");
        Assert.True(await runtime.SubmitPersistedUserTextAsync(
            "third",
            thirdEventId,
            behavior: UserTextBehavior.Queue));
        var accepted = await turns.OpenAsync(runtime.SessionId);
        Assert.Single(accepted);
        Assert.Equal(AgentRunStatus.Running, accepted[0].Status);
        Assert.Equal([queuedEventId, thirdEventId], runtime.Snapshot.PendingAgentInputIds);
        await runtime.DetachAsync();
        model.Release.TrySetResult();
        await runtime.WaitUntilIdleAsync();

        var assistants = runtime.Snapshot.Entries.Where(entry => entry.Role == ConversationRole.Assistant).ToArray();
        Assert.Equal(2, assistants.Length);
        Assert.All(assistants, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Text)));
        Assert.Equal(
            ["first", "second", "third"],
            runtime.Snapshot.Entries.Where(entry => entry.Role == ConversationRole.User).Select(entry => entry.Text).ToArray());
        Assert.Empty(await turns.OpenAsync(runtime.SessionId));
    }

    [Fact]
    public async Task Transport_detach_then_reattach_ready_restores_open_execution_projection()
    {
        var output = new CapturingSessionOutput();
        var turns = new RuntimeAgentRunStore();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero));
        var model = new HoldingLanguageModel();
        await using var runtime = CreateRuntime(output, model, time, turns);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("[test:durable-stream] resume");
        await output.WaitForAsync(item => item.Payload is TextDeltaOutput);
        var open = await turns.OpenAsync(runtime.SessionId);
        var executionId = Assert.Single(open).AgentRunId;
        var responseId = runtime.ActiveResponseId;
        Assert.NotNull(responseId);

        await runtime.TransportDetachAsync();
        Assert.True(await runtime.AttachAsync());
        await runtime.WaitUntilMailboxDrainedAsync();

        var ready = Assert.IsType<ReadyOutput>(
            output.Items.Last(item => item.Payload is ReadyOutput).Payload);
        Assert.Equal(executionId, ready.Ready.AgentRunId);
        Assert.Equal(responseId, ready.Ready.ActiveResponseId);
        var streaming = Assert.Single(
            ready.Ready.History,
            entry => entry.ResponseId == responseId);
        Assert.Equal(EntryStatus.Streaming, streaming.Status);
        Assert.False(string.IsNullOrEmpty(streaming.Text));

        model.Release.TrySetResult();
        await runtime.WaitUntilIdleAsync();
    }

    [Fact]
    public async Task Stop_cancels_durable_execution_after_interrupted_assistant_persists()
    {
        var turns = new RuntimeAgentRunStore();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero));
        var model = new HoldingLanguageModel();
        await using var runtime = CreateRuntime(new CapturingSessionOutput(), model, time, turns);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("hi");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (runtime.ActiveResponseId is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.NotNull(runtime.ActiveResponseId);
        Assert.Equal(ResponseCancelResult.Cancelled, await runtime.CancelResponseAsync(runtime.ActiveResponseId.Value));
        model.Release.TrySetResult();
        await runtime.WaitUntilIdleAsync();

        var assistant = runtime.Snapshot.Entries.Single(entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal(EntryStatus.Interrupted, assistant.Status);
        Assert.Empty(await turns.OpenAsync(runtime.SessionId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Background_start_returns_committed_child_promptly_and_repeated_tool_call_returns_same_child(bool reportCompletion)
    {
        var memory = new InMemoryMemoryStore();
        var runs = new RuntimeAgentRunStore();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero));
        var definition = SampleDefinitions.Examiner with
        { InitiativePolicy = SampleDefinitions.Examiner.InitiativePolicy with { Enabled = false },
            Environment = new AgentCore.Domain.Definitions.RoleEnvironment(ToolAllowlist: [AgentCore.Application.Tools.ToolCatalog.BackgroundStart]) };
        var snapshot = RuntimeAgentRunStore.WithPins(new SessionSnapshot(1, Guid.NewGuid(), 1, definition, SessionMode.Text, null,
            SessionStatus.Created, [], "", 0, null, null, time.GetUtcNow(), time.GetUtcNow(), AgentInstanceId: Guid.NewGuid(),
            ModelSelection: new("synthetic-offline/scripted", "primary-llm", "scripted", ModelSelectionSource.Host, null)));
        var model = new ImmediateBackgroundModel(reportCompletion);
        await using var runtime = CreateRuntime(new CapturingSessionOutput(), model, time, runs, memory, snapshot);
        await runtime.AttachAsync();
        await runtime.SubmitPersistedUserTextAsync("Do this in the background", Guid.NewGuid());
        await runtime.WaitUntilIdleAsync();
        var results = model.Results;
        Assert.Equal(2, results.Count);
        Assert.Equal(results[0].GetProperty("backgroundSessionId").GetGuid(), results[1].GetProperty("backgroundSessionId").GetGuid());
        Assert.True(results[0].GetProperty("started").GetBoolean());
        Assert.True(results[1].GetProperty("alreadyStarted").GetBoolean());
        var childId = results[0].GetProperty("backgroundSessionId").GetGuid();
        var child = (await memory.LoadAsync(childId))!;
        Assert.Equal(SessionOriginKind.ImmediateBackground, child.Origin.Kind);
        Assert.Equal(runtime.SessionId, child.Origin.OriginatingSessionId);
        Assert.Equal(runtime.Snapshot.AgentInstanceId, child.AgentInstanceId);
        Assert.Equal(SessionSurface.BackgroundWork, child.Surfaces);
        var owner = new AgentRunOwner(child.AgentInstanceId, child.ProfileId!.Value);
        var childRun = Assert.Single(await runs.ListForSessionAsync(owner, childId));
        Assert.Equal(AgentRunStatus.Queued, childRun.Status);
        Assert.Equal(ActivationKind.ImmediateBackground, childRun.Admission.Activation.Kind);
        Assert.Equal(AgentRunStatus.Completed, Assert.Single(await runs.ListForSessionAsync(owner, runtime.SessionId)).Status);
        var now = time.GetUtcNow();
        childRun = await runs.ApplyAsync(owner, childRun.AgentRunId, new AgentCore.Application.Execution.AgentRunCommand.Claim(
            childRun.Revision, now, Guid.NewGuid(), now.AddMinutes(5)));
        await using var childRuntime = CreateRuntime(new CapturingSessionOutput(), new BackgroundCompletionModel("Response", false), time, runs, memory, child);
        Assert.True(await childRuntime.DispatchAgentRunAsync(childRun.AgentRunId, headless: true));
        await childRuntime.WaitUntilIdleAsync();
        if (!reportCompletion)
        {
            Assert.Empty(await runs.ListUnreportedCompletionsAsync(8));
            Assert.Equal("notRequested", (await runs.GetCompletionDeliveryAsync(owner, childRun.AgentRunId)).Status);
            Assert.Equal(AgentRunStatus.Completed, (await runs.GetAsync(owner, childRun.AgentRunId))!.Status);
            Assert.Single(await runs.ListForSessionAsync(owner, runtime.SessionId));
            return;
        }
        var candidate = Assert.Single(await runs.ListUnreportedCompletionsAsync(8));
        Assert.True(await runtime.AdmitBackgroundCompletionAsync(candidate));
        Assert.True(await runtime.AdmitBackgroundCompletionAsync(candidate));
        await runtime.WaitUntilIdleAsync();
        Assert.Empty(await runs.ListUnreportedCompletionsAsync(8));
        var report = Assert.Single(await runs.ListForSessionAsync(owner, runtime.SessionId), item => item.Admission.Activation.Kind == ActivationKind.BackgroundCompleted);
        report = await runs.ApplyAsync(owner, report.AgentRunId, new AgentCore.Application.Execution.AgentRunCommand.Claim(
            report.Revision, now, Guid.NewGuid(), now.AddMinutes(5)));
        Assert.True(await runtime.DispatchAgentRunAsync(report.AgentRunId, headless: false));
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(AgentRunStatus.Completed, (await runs.GetAsync(owner, report.AgentRunId))!.Status);
        Assert.Equal(2, runtime.Snapshot.Entries.Count(entry => entry.Role == ConversationRole.Assistant));
        Assert.True(await childRuntime.AttachAsync());
        Assert.True(await childRuntime.SubmitPersistedUserTextAsync("Continue the completed task", Guid.NewGuid()));
        await childRuntime.WaitUntilIdleAsync();
        Assert.Empty(await runs.ListUnreportedCompletionsAsync(8));
        Assert.Equal(2, (await runs.ListForSessionAsync(owner, runtime.SessionId)).Count);
    }

    private sealed class ImmediateBackgroundModel(bool reportCompletion) : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: true);
        public List<System.Text.Json.JsonElement> Results { get; } = [];
        private int _requests;
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            _requests++;
            if (_requests > 1 && request.Messages.Any(message => message.Role == ModelRole.Tool))
                Results.Add(System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(request.Messages.Last(message => message.Role == ModelRole.Tool).Text));
            if (_requests <= 2)
            {
                yield return new ModelToolCallEvent(new("same-start", AgentCore.Application.Tools.ToolCatalog.BackgroundStart,
                    System.Text.Json.JsonSerializer.Serialize(new { objective = "Inspect the bounded task", title = "Task inspection", reportCompletion })));
                yield return new ModelCompleted(ModelStopReason.ToolCalls);
            }
            else
            {
                yield return new ModelDisplayDelta("Started in the background.");
                yield return new ModelSemanticResponseReady(new("Started in the background.", new(ModelSpeechMode.None, null), []));
                yield return new ModelCompleted(ModelStopReason.Completed);
            }
        }
    }

    [Theory]
    [InlineData("NoAction", false, AgentRunOutcomeKind.NoAction)]
    [InlineData("Response", false, AgentRunOutcomeKind.Response)]
    [InlineData("NeedsAttention", true, AgentRunOutcomeKind.NeedsAttention)]
    public async Task Headless_background_completion_commits_one_outcome_and_continues_in_same_session(
        string outcome, bool attention, AgentRunOutcomeKind expected)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero));
        var memory = new InMemoryMemoryStore();
        var runs = new RuntimeAgentRunStore();
        runs.Bind(memory);
        var sessionId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var responseId = Guid.NewGuid();
        var now = time.GetUtcNow();
        var input = new ConversationEntry(Guid.NewGuid(), 1, Guid.NewGuid(), ConversationRole.User,
            "Check the background objective", null, EntryStatus.Completed, SessionMode.Text, 0, 30, now);
        var snapshot = RuntimeAgentRunStore.WithPins(new SessionSnapshot(1, sessionId, 1, SampleDefinitions.Examiner,
            SessionMode.Text, null, SessionStatus.Created, [input], "", 0, null, null, now, now,
            AgentInstanceId: Guid.NewGuid(), ModelSelection: new("synthetic-offline/scripted", "primary-llm", "scripted", ModelSelectionSource.Host, null),
            Origin: new(SessionOriginKind.ManualBackground, initialBackgroundAgentRunId: runId), Surfaces: SessionSurface.BackgroundWork));
        var activation = new Activation(Guid.NewGuid(), sessionId, ActivationKind.ManualBackground, [input.EntryId],
            input.SourceEventId, null, null, null, "manual:test", now);
        var run = AgentRun.Create(runId, new(snapshot.AgentInstanceId, snapshot.ProfileId!.Value),
            new(activation, snapshot.Definition.Id, snapshot.Definition.Version, snapshot.PinnedPersona!, responseId, AgentRunOutputContract.BackgroundOutcome),
            new("synthetic-offline/scripted", "primary-llm", "scripted", null), 3, now);
        await runs.AdmitAsync(snapshot, 0, run);
        run = await runs.ApplyAsync(run.Owner, runId, new AgentCore.Application.Execution.AgentRunCommand.Claim(
            run.Revision, now, Guid.NewGuid(), now.AddMinutes(5)));
        var output = new CapturingSessionOutput();
        var model = new BackgroundCompletionModel(outcome, attention);
        await using var runtime = CreateRuntime(output, model, time, runs, memory, snapshot);
        Assert.True(await runtime.DispatchAgentRunAsync(runId, headless: true));
        await runtime.WaitUntilIdleAsync();
        var completed = (await runs.GetAsync(run.Owner, runId))!;
        Assert.Equal(AgentRunStatus.Completed, completed.Status);
        Assert.Equal(expected, completed.Result!.OutcomeKind);
        var durable = (await memory.LoadAsync(sessionId))!;
        Assert.Equal(expected == AgentRunOutcomeKind.NoAction ? 0 : 1, durable.Entries.Count(entry => entry.Role == ConversationRole.Assistant));
        Assert.DoesNotContain(output.Items, item => item.Payload is TextDeltaOutput);
        Assert.Equal(SessionSurface.BackgroundWork, durable.Surfaces);
        Assert.True(await runtime.AttachAsync());
        Assert.True(await runtime.SubmitPersistedUserTextAsync("Continue this task", Guid.NewGuid()));
        await runtime.WaitUntilIdleAsync();
        var all = await runs.ListForSessionAsync(run.Owner, sessionId);
        Assert.Equal(2, all.Count);
        Assert.All(all, item => Assert.Equal(AgentRunStatus.Completed, item.Status));
        Assert.All(all, item => Assert.Equal(sessionId, item.SessionId));
        Assert.Equal(ActivationKind.UserTurn, all.Single(item => item.AgentRunId != runId).Admission.Activation.Kind);
    }

    [Fact]
    public async Task Headless_retry_retains_ownership_while_the_next_brain_decision_is_pending()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero));
        var memory = new InMemoryMemoryStore();
        var runs = new RuntimeAgentRunStore();
        runs.Bind(memory);
        var sessionId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var responseId = Guid.NewGuid();
        var now = time.GetUtcNow();
        var input = new ConversationEntry(Guid.NewGuid(), 1, Guid.NewGuid(), ConversationRole.User,
            "Check the background objective", null, EntryStatus.Completed, SessionMode.Text, 0, 30, now);
        var snapshot = RuntimeAgentRunStore.WithPins(new SessionSnapshot(1, sessionId, 1, SampleDefinitions.Examiner,
            SessionMode.Text, null, SessionStatus.Created, [input], "", 0, null, null, now, now,
            AgentInstanceId: Guid.NewGuid(), ModelSelection: new("synthetic-offline/scripted", "primary-llm", "scripted", ModelSelectionSource.Host, null),
            Origin: new(SessionOriginKind.ManualBackground, initialBackgroundAgentRunId: runId), Surfaces: SessionSurface.BackgroundWork));
        var activation = new Activation(Guid.NewGuid(), sessionId, ActivationKind.ManualBackground, [input.EntryId],
            input.SourceEventId, null, null, null, "manual:test", now);
        var run = AgentRun.Create(runId, new(snapshot.AgentInstanceId, snapshot.ProfileId!.Value),
            new(activation, snapshot.Definition.Id, snapshot.Definition.Version, snapshot.PinnedPersona!, responseId, AgentRunOutputContract.BackgroundOutcome),
            new("synthetic-offline/scripted", "primary-llm", "scripted", null), 3, now);
        await runs.AdmitAsync(snapshot, 0, run);
        run = await runs.ApplyAsync(run.Owner, runId, new AgentCore.Application.Execution.AgentRunCommand.Claim(
            run.Revision, now, Guid.NewGuid(), now.AddMinutes(5)));
        var brain = new GatedRetryBrain();
        await using var runtime = CreateRuntime(new CapturingSessionOutput(), new RetryingBackgroundModel(),
            time, runs, memory, snapshot, brain: brain);
        Assert.True(await runtime.DispatchAgentRunAsync(runId, headless: true));
        await runtime.WaitUntilIdleAsync();
        var waiting = (await runs.GetAsync(run.Owner, runId))!;
        Assert.Equal(AgentRunStatus.WaitingToRetry, waiting.Status);
        Assert.False(await runtime.HasAcceptedConversationWorkAsync());
        time.Advance(TimeSpan.FromSeconds(10));
        run = await runs.ApplyAsync(run.Owner, runId, new AgentCore.Application.Execution.AgentRunCommand.Claim(
            waiting.Revision, time.GetUtcNow(), Guid.NewGuid(), time.GetUtcNow().AddMinutes(5)));
        Assert.True(await runtime.DispatchAgentRunAsync(runId, headless: true));
        await brain.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            // Cleanup must keep the new claimed attempt even though the previous response was terminal.
            Assert.True(await runtime.HasAcceptedConversationWorkAsync());
            await runtime.TransportDetachAsync();
            Assert.True(await runtime.HasAcceptedConversationWorkAsync());
        }
        finally { brain.Release.TrySetResult(); }
        await runtime.WaitUntilIdleAsync();
        var completed = (await runs.GetAsync(run.Owner, runId))!;
        Assert.Equal(AgentRunStatus.Completed, completed.Status);
        Assert.Equal(2, completed.AttemptCount);
        Assert.Equal(run.ResponseId, completed.ResponseId);
        Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant);
        Assert.False(await runtime.HasAcceptedConversationWorkAsync());
    }

    private sealed class GatedRetryBrain : IAgentBrain
    {
        private int calls;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<AgentDecision> DecideAsync(AgentContext context, Guid responseId, CancellationToken ct = default)
        {
            if (++calls == 2)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(ct);
            }
            return new Speak(new PromptContextBuilder().Build(context, responseId));
        }
    }

    private sealed class RetryingBackgroundModel : ILanguageModel
    {
        private int calls;
        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: true);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            if (++calls == 1)
            {
                yield return new ModelFailed(new(ProviderErrorCode.Unavailable, "Provider unavailable."));
                yield break;
            }
            yield return new ModelToolCallEvent(new("complete", AgentCore.Application.Tools.ToolCatalog.WorkComplete,
                "{\"summary\":\"Recovered background result.\",\"outcome\":\"Response\",\"attentionRequired\":false}"));
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
        }
    }

    private sealed class BackgroundCompletionModel(string outcome, bool attention) : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: true);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Tools?.Any(tool => tool.Name == AgentCore.Application.Tools.ToolCatalog.WorkComplete) == true)
            {
                yield return new ModelToolCallEvent(new("complete", AgentCore.Application.Tools.ToolCatalog.WorkComplete,
                    System.Text.Json.JsonSerializer.Serialize(new { summary = "Background checked.", attentionRequired = attention, outcome })));
                yield return new ModelCompleted(ModelStopReason.ToolCalls);
            }
            else
            {
                yield return new ModelDisplayDelta("Continued in the same Session.");
                yield return new ModelSemanticResponseReady(new("Continued in the same Session.", new(ModelSpeechMode.None, null), []));
                yield return new ModelCompleted(ModelStopReason.Completed);
            }
        }
    }

    private static SessionRuntime CreateRuntime(
        ISessionOutput output,
        ILanguageModel model,
        FakeTimeProvider time,
        RuntimeAgentRunStore agentRuns,
        InMemoryMemoryStore? memory = null,
        SessionSnapshot? snapshot = null,
        IAgentRunAuthority? authority = null,
        IAgentBrain? brain = null)
    {
        var prefix = snapshot?.SessionId.ToString("N")[..8] ?? "019944af";
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 256).Select(index => Guid.Parse($"{prefix}-0000-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842")]);
        memory ??= new InMemoryMemoryStore();
        var now = time.GetUtcNow();
        snapshot ??= new SessionSnapshot(
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
            now,
            ModelSelection: new SessionModelSelection(
                "synthetic-offline/scripted",
                "primary-llm",
                "scripted",
                ModelSelectionSource.SystemDefault,
                null), AgentInstanceId: Guid.NewGuid());
        snapshot = RuntimeAgentRunStore.WithPins(snapshot);
        agentRuns.Bind(memory);
        if (snapshot.Revision == 1 && snapshot.Entries.Count == 0)
        {
            memory.SaveAsync(snapshot, 0).AsTask().GetAwaiter().GetResult();
        }

        return SessionRuntimeFixture.Create(
            snapshot,
            model,
            brain ?? new DefaultAgentBrain(new PromptContextBuilder()),
            memory,
            output,
            ids,
            time,
            NullLogger<SessionRuntime>.Instance,
            new FakeInterruptionClassifier(),
            agentRuns: agentRuns, runAuthority: authority);
    }

    private sealed class DeniedAuthority : IAgentRunAuthority
    {
        public ValueTask<AgentCore.Domain.Definitions.AgentDefinition?> CurrentDefinitionAsync(AgentRun run, CancellationToken ct = default) =>
            ValueTask.FromResult<AgentCore.Domain.Definitions.AgentDefinition?>(null);
    }

    private sealed class CountingLanguageModel : ILanguageModel
    {
        public int Requests { get; private set; }
        public ModelCapabilities Capabilities { get; } = new(true, true);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests++;
            await Task.CompletedTask;
            yield return new ModelTextDelta("Unexpected provider request");
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }

    private sealed class HoldingLanguageModel : ILanguageModel
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ModelCapabilities Capabilities { get; } = new(true, true);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ModelTextDelta("T1");
            await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }
}
