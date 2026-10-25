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

public sealed partial class AgentRunDurabilityTests
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
                "dca20000-0000-4000-8000-000000000001",
                "scripted",
                ModelSelectionSource.TenantDefault,
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
