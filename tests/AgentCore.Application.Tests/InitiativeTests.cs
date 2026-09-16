using AgentCore.Application.Agents;
using AgentCore.Application.Events;
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

public sealed class InitiativeTests
{
    [Fact]
    public async Task Idle_offers_one_long_silence_hint_per_silence_period()
    {
        var time = Clock();
        var output = new CapturingSessionOutput();
        var brain = new RecordingAgentBrain(new DefaultAgentBrain(new PromptContextBuilder()));
        await using var runtime = Create(output, new ScriptedLanguageModel(["Need a hint?"]), time, brain);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello?");
        await runtime.WaitUntilIdleAsync();
        var started = CountStarted(output, "LongSilence");
        await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(started + 1, CountStarted(output, "LongSilence"));
        var afterHint = CountStarted(output, "LongSilence");
        await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
        await runtime.WaitUntilMailboxDrainedAsync();
        Assert.Equal(afterHint, CountStarted(output, "LongSilence"));
    }

    [Fact]
    public async Task Stay_silent_applies_cooldown_without_response_started()
    {
        var time = Clock();
        var output = new CapturingSessionOutput();
        var brain = new RecordingAgentBrain(new DefaultAgentBrain(new PromptContextBuilder()));
        await using var runtime = Create(output, new ScriptedLanguageModel(), time, brain);
        await runtime.AttachAsync();
        await runtime.WaitUntilMailboxDrainedAsync();
        await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(0, CountStarted(output, "LongSilence"));
        var calls = brain.Calls;
        await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
        await runtime.WaitUntilMailboxDrainedAsync();
        Assert.Equal(calls, brain.Calls);
        time.Advance(TimeSpan.FromSeconds(30));
        await runtime.WaitUntilMailboxDrainedAsync();
        await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
        await runtime.WaitUntilIdleAsync();
        Assert.True(brain.Calls > calls);
        Assert.Equal(0, CountStarted(output, "LongSilence"));
    }

    [Fact]
    public async Task User_input_invalidates_pending_idle_initiative()
    {
        var time = Clock();
        var output = new CapturingSessionOutput();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new RecordingAgentBrain(new DefaultAgentBrain(new PromptContextBuilder()));
        var brain = new GatedInitiativeBrain(inner, gate);
        await using var runtime = Create(output, new ScriptedLanguageModel(["Need a hint?"]), time, brain);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello?");
        await runtime.WaitUntilIdleAsync();
        await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
        await brain.InitiativeCalled.Task;
        await runtime.SubmitUserTextAsync("I am here");
        await runtime.WaitUntilMailboxDrainedAsync();
        gate.TrySetResult();
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(0, CountStarted(output, "LongSilence"));
        Assert.Contains(TriggerKind.UserTurn, inner.Triggers);
    }

    [Fact]
    public async Task Environment_update_is_queued_during_output_and_deduped()
    {
        var time = Clock();
        var output = new CapturingSessionOutput();
        var model = new GatedThenLiveModel();
        var brain = new RecordingAgentBrain(new DefaultAgentBrain(new PromptContextBuilder()));
        var classifier = new FakeInterruptionClassifier();
        await using var runtime = Create(
            output,
            model,
            time,
            brain,
            SampleDefinitions.Support,
            classifier);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello");
        await output.WaitForAsync(item => item.Payload is TextDeltaOutput);
        var eventId = Guid.Parse("019944af-0000-7000-8000-0000000000ee");
        await runtime.SubmitEnvironmentAsync(SyntheticEnvironmentDriver.OrderShipped(eventId, "A-1"));
        await runtime.SubmitEnvironmentAsync(SyntheticEnvironmentDriver.OrderShipped(eventId, "A-1"));
        await runtime.SubmitEnvironmentAsync(
            new EnvironmentEvent(Guid.Parse("019944af-0000-7000-8000-0000000000ef"), "weather", new Dictionary<string, string>()));
        model.Gate.TrySetResult();
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(1, brain.Triggers.Count(kind => kind == TriggerKind.EnvironmentUpdate));
        Assert.Contains(output.Items, item => item.Payload is ResponseStartedOutput started && started.Trigger == "EnvironmentUpdate");
        Assert.Equal(0, classifier.Calls);
    }

    [Fact]
    public async Task Environment_event_expires_after_thirty_seconds()
    {
        var time = Clock();
        var output = new CapturingSessionOutput();
        var model = new GatedThenLiveModel();
        var brain = new RecordingAgentBrain(new DefaultAgentBrain(new PromptContextBuilder()));
        await using var runtime = Create(output, model, time, brain, SampleDefinitions.Support);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello");
        await output.WaitForAsync(item => item.Payload is TextDeltaOutput);
        await runtime.SubmitEnvironmentAsync(
            SyntheticEnvironmentDriver.OrderShipped(Guid.Parse("019944af-0000-7000-8000-0000000000f0"), "B-2", "delayed"));
        time.Advance(TimeSpan.FromSeconds(31));
        await runtime.WaitUntilMailboxDrainedAsync();
        model.Gate.TrySetResult();
        await runtime.WaitUntilIdleAsync();
        Assert.DoesNotContain(TriggerKind.EnvironmentUpdate, brain.Triggers);
    }

    [Fact]
    public async Task Detached_session_does_not_offer_initiative()
    {
        var time = Clock();
        var output = new CapturingSessionOutput();
        var brain = new RecordingAgentBrain(new DefaultAgentBrain(new PromptContextBuilder()));
        await using var runtime = Create(output, new ScriptedLanguageModel(), time, brain, SampleDefinitions.Support);
        await runtime.AttachAsync();
        await runtime.WaitUntilMailboxDrainedAsync();
        var generation = runtime.TimerGeneration;
        await runtime.DetachAsync();
        await runtime.WaitUntilMailboxDrainedAsync();
        var calls = brain.Calls;
        await runtime.SubmitTimerElapsedAsync("idle", generation);
        await runtime.SubmitEnvironmentAsync(
            SyntheticEnvironmentDriver.OrderShipped(Guid.Parse("019944af-0000-7000-8000-0000000000f1"), "C-3"));
        await runtime.WaitUntilMailboxDrainedAsync();
        Assert.Equal(calls, brain.Calls);
        Assert.Equal(0, CountStarted(output, "LongSilence"));
        Assert.Equal(0, CountStarted(output, "EnvironmentUpdate"));
    }

    [Fact]
    public async Task Unfinished_topic_can_speak_once_and_empty_topic_clears_pending()
    {
        var time = Clock();
        var output = new CapturingSessionOutput();
        var brain = new RecordingAgentBrain(new DefaultAgentBrain(new PromptContextBuilder()));
        await using var runtime = Create(output, new ScriptedLanguageModel(["Still there?"]), time, brain, SampleDefinitions.Support);
        await runtime.AttachAsync();
        await runtime.SubmitEnvironmentAsync(
            SyntheticEnvironmentDriver.Unfinished(Guid.Parse("019944af-0000-7000-8000-0000000000f2"), "return"));
        await runtime.WaitUntilIdleAsync();
        Assert.Contains(TriggerKind.UnfinishedInteraction, brain.Triggers);
        Assert.Null(runtime.Snapshot.PendingTopic);
        await runtime.SubmitEnvironmentAsync(
            SyntheticEnvironmentDriver.Unfinished(Guid.Parse("019944af-0000-7000-8000-0000000000f3"), "warranty"));
        await runtime.WaitUntilIdleAsync();
        await runtime.SubmitEnvironmentAsync(
            SyntheticEnvironmentDriver.Unfinished(Guid.Parse("019944af-0000-7000-8000-0000000000f4"), string.Empty));
        await runtime.WaitUntilMailboxDrainedAsync();
        Assert.Null(runtime.Snapshot.PendingTopic);
    }

    private static int CountStarted(CapturingSessionOutput output, string trigger) =>
        output.Items.Count(item => item.Payload is ResponseStartedOutput started && started.Trigger == trigger);

    private static FakeTimeProvider Clock() =>
        new(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));

    private static SessionRuntime Create(
        ISessionOutput output,
        ILanguageModel model,
        FakeTimeProvider time,
        IAgentBrain brain,
        AgentDefinition? definition = null,
        IInterruptionClassifier? classifier = null)
    {
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 256).Select(index => Guid.Parse($"019944af-0000-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842")]);
        var store = new InMemoryMemoryStore();
        var now = time.GetUtcNow();
        var snapshot = new SessionSnapshot(
            1,
            ids.NewSessionId(),
            1,
            definition ?? SampleDefinitions.Examiner,
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
            brain,
            store,
            output,
            ids,
            time,
            NullLogger<SessionRuntime>.Instance,
            classifier);
    }

    private sealed class GatedInitiativeBrain(IAgentBrain inner, TaskCompletionSource gate) : IAgentBrain
    {
        public TaskCompletionSource InitiativeCalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<AgentDecision> DecideAsync(
            AgentContext context,
            Guid responseId,
            CancellationToken cancellationToken = default)
        {
            if (context.Trigger.Kind != TriggerKind.UserTurn)
            {
                InitiativeCalled.TrySetResult();
                await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return await inner.DecideAsync(context, responseId, cancellationToken).ConfigureAwait(false);
        }
    }
}
