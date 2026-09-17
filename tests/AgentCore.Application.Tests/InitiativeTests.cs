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
    public async Task Default_brain_allows_repeated_long_silence_hints_within_max_per_silence_period()
    {
        var time = Clock();
        var output = new CapturingSessionOutput();
        var definition = RepeatPolicy(maxPerSilence: 2, consecutiveCap: 2);
        var brain = new RecordingAgentBrain(new DefaultAgentBrain(new PromptContextBuilder()));
        await using var runtime = Create(
            output,
            new ScriptedLanguageModel(["Still there?", "Follow up?", "Third?"]),
            time,
            brain,
            definition);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello?");
        await runtime.WaitUntilIdleAsync();
        await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(1, CountStarted(output, "LongSilence"));
        time.Advance(TimeSpan.FromSeconds(30));
        await runtime.WaitUntilMailboxDrainedAsync();
        await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(2, CountStarted(output, "LongSilence"));
        time.Advance(TimeSpan.FromSeconds(30));
        await runtime.WaitUntilMailboxDrainedAsync();
        await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
        await runtime.WaitUntilMailboxDrainedAsync();
        Assert.Equal(2, CountStarted(output, "LongSilence"));
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
        Assert.Equal(OutputActivity.Idle, runtime.Output);
        Assert.Contains(
            output.Items,
            item => item.Payload is StateChangedOutput state
                && state.OutputState == nameof(OutputActivity.WaitingForAgent));
        Assert.Contains(
            output.Items,
            item => item.Payload is StateChangedOutput state
                && state.OutputState == nameof(OutputActivity.Idle));
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

    [Fact]
    public async Task Repeated_idle_speaks_three_times_then_deactivates_at_cap()
    {
        var time = Clock();
        var output = new CapturingSessionOutput();
        var definition = RepeatPolicy(maxPerSilence: 3, consecutiveCap: 3);
        var brain = new ScriptedProactiveBrain(definition);
        await using var runtime = Create(
            output,
            new ScriptedLanguageModel(["Need a hint?", "Still there?", "One more?", "Last call."]),
            time,
            brain,
            definition);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello?");
        await runtime.WaitUntilIdleAsync();
        for (var i = 0; i < 3; i++)
        {
            if (i > 0)
            {
                time.Advance(TimeSpan.FromSeconds(5));
                await runtime.WaitUntilMailboxDrainedAsync();
            }

            await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
            await runtime.WaitUntilIdleAsync();
            Assert.Equal(i + 1, CountStarted(output, "LongSilence"));
        }

        time.Advance(TimeSpan.FromSeconds(5));
        await runtime.WaitUntilMailboxDrainedAsync();
        await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(3, CountStarted(output, "LongSilence"));
        Assert.Equal(SessionStatus.Attached, runtime.Snapshot.Status);
    }

    [Fact]
    public async Task Zero_cap_never_speaks_and_stays_attached_until_silent_bounds()
    {
        var time = Clock();
        var output = new CapturingSessionOutput();
        var definition = RepeatPolicy(maxPerSilence: 1, consecutiveCap: 0);
        await using var runtime = Create(output, new ScriptedLanguageModel(), time, new DefaultAgentBrain(new PromptContextBuilder()), definition);
        await runtime.AttachAsync();
        await runtime.WaitUntilMailboxDrainedAsync();
        await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(0, CountStarted(output, "LongSilence"));
        Assert.Equal(SessionStatus.Attached, runtime.Snapshot.Status);
    }

    [Fact]
    public async Task At_cap_denies_speak_and_request_deactivate_is_idempotent()
    {
        var time = Clock();
        var output = new CapturingSessionOutput();
        var definition = RepeatPolicy(maxPerSilence: 1, consecutiveCap: 1);
        var brain = new ScriptedProactiveBrain(definition);
        await using var runtime = Create(
            output,
            new ScriptedLanguageModel(["Need a hint?", "Should not speak."]),
            time,
            brain,
            definition);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello?");
        await runtime.WaitUntilIdleAsync();
        await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(1, CountStarted(output, "LongSilence"));
        time.Advance(TimeSpan.FromSeconds(5));
        await runtime.WaitUntilMailboxDrainedAsync();
        await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(1, CountStarted(output, "LongSilence"));
        Assert.Equal(SessionStatus.Attached, runtime.Snapshot.Status);
        Assert.NotEmpty(runtime.Snapshot.Entries);
    }

    [Fact]
    public async Task User_activity_resets_consecutive_cap()
    {
        var time = Clock();
        var output = new CapturingSessionOutput();
        var definition = RepeatPolicy(maxPerSilence: 1, consecutiveCap: 1);
        var brain = new ScriptedProactiveBrain(definition);
        await using var runtime = Create(
            output,
            new ScriptedLanguageModel(["Need a hint?", "Here.", "Again?"]),
            time,
            brain,
            definition);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello?");
        await runtime.WaitUntilIdleAsync();
        await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(1, CountStarted(output, "LongSilence"));
        await runtime.SubmitUserTextAsync("I am back");
        await runtime.WaitUntilIdleAsync();
        time.Advance(TimeSpan.FromSeconds(5));
        await runtime.WaitUntilMailboxDrainedAsync();
        await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(2, CountStarted(output, "LongSilence"));
        Assert.Equal(SessionStatus.Attached, runtime.Snapshot.Status);
    }

    [Fact]
    public async Task Silent_evaluation_cap_deactivates_always_stay_silent()
    {
        var time = Clock();
        var output = new CapturingSessionOutput();
        var definition = RepeatPolicy(maxPerSilence: 3, consecutiveCap: 3, silentEvaluations: 2);
        var brain = new StaySilentBrain();
        await using var runtime = Create(output, new ScriptedLanguageModel(), time, brain, definition);
        await runtime.AttachAsync();
        await runtime.WaitUntilMailboxDrainedAsync();
        await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(0, CountStarted(output, "LongSilence"));
        Assert.Equal(SessionStatus.Attached, runtime.Snapshot.Status);
        time.Advance(TimeSpan.FromSeconds(5));
        await runtime.WaitUntilMailboxDrainedAsync();
        await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(SessionStatus.Attached, runtime.Snapshot.Status);
        time.Advance(TimeSpan.FromSeconds(5));
        await runtime.WaitUntilMailboxDrainedAsync();
        await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(SessionStatus.Paused, runtime.Snapshot.Status);
    }

    [Fact]
    public async Task Live_response_and_initiative_hold_block_idle_speak()
    {
        var time = Clock();
        var output = new CapturingSessionOutput();
        var model = new GatedThenLiveModel();
        var definition = RepeatPolicy(maxPerSilence: 3, consecutiveCap: 3);
        var brain = new ScriptedProactiveBrain(definition);
        await using var runtime = Create(output, model, time, brain, definition);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello?");
        await output.WaitForAsync(item => item.Payload is TextDeltaOutput);
        var duringLive = CountStarted(output, "LongSilence");
        await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
        await runtime.WaitUntilMailboxDrainedAsync();
        Assert.Equal(duringLive, CountStarted(output, "LongSilence"));
        model.Gate.TrySetResult();
        await runtime.WaitUntilIdleAsync();
        await runtime.SubmitInitiativeHoldAsync(true);
        await runtime.WaitUntilMailboxDrainedAsync();
        time.Advance(TimeSpan.FromSeconds(5));
        await runtime.WaitUntilMailboxDrainedAsync();
        var calls = brain.Calls;
        await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
        await runtime.WaitUntilMailboxDrainedAsync();
        Assert.Equal(calls, brain.Calls);
    }

    [Fact]
    public async Task Pending_upload_holds_idle_initiative()
    {
        var time = Clock();
        var output = new CapturingSessionOutput();
        var attachments = new InMemoryAttachmentStore(time);
        var definition = RepeatPolicy(maxPerSilence: 3, consecutiveCap: 3);
        var brain = new ScriptedProactiveBrain(definition);
        await using var runtime = Create(
            output,
            new ScriptedLanguageModel(["Need a hint?"]),
            time,
            brain,
            definition,
            attachments: attachments);
        await runtime.AttachAsync();
        var uploaded = await attachments.UploadPendingAsync(
            runtime.SessionId,
            "notes.txt",
            "text/plain",
            new MemoryStream("hi"u8.ToArray()),
            false);
        await runtime.StageAttachmentsAsync([uploaded.AttachmentId]);
        await runtime.WaitUntilMailboxDrainedAsync();
        var calls = brain.Calls;
        await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
        await runtime.WaitUntilMailboxDrainedAsync();
        Assert.Equal(calls, brain.Calls);
        Assert.Equal(0, CountStarted(output, "LongSilence"));
    }

    [Fact]
    public async Task Default_brain_allows_second_long_silence_when_prior_assistant_text_does_not_end_with_question_mark()
    {
        var time = Clock();
        var output = new CapturingSessionOutput();
        var brain = new RecordingAgentBrain(new DefaultAgentBrain(new PromptContextBuilder()));
        var definition = RepeatPolicy(maxPerSilence: 5, consecutiveCap: 5);
        await using var runtime = Create(
            output,
            new ScriptedLanguageModel(["Hello from synthetic.", "Following up without a question mark."]),
            time,
            brain,
            definition);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hi");
        await runtime.WaitUntilIdleAsync();
        Assert.DoesNotContain('?', runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant).Text);

        await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(1, CountStarted(output, "LongSilence"));

        time.Advance(TimeSpan.FromSeconds(5));
        await runtime.WaitUntilMailboxDrainedAsync();
        await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(2, CountStarted(output, "LongSilence"));
        Assert.Equal(2, brain.Triggers.Count(kind => kind == TriggerKind.LongSilence));
    }

    [Fact]
    public async Task Default_brain_emits_three_consecutive_proactive_messages_without_user_input()
    {
        var time = Clock();
        var output = new CapturingSessionOutput();
        var definition = RepeatPolicy(maxPerSilence: 5, consecutiveCap: 5);
        await using var runtime = Create(
            output,
            new ScriptedLanguageModel(
                ["Hello from synthetic.", "Proactive two.", "Proactive three.", "Proactive four."]),
            time,
            new DefaultAgentBrain(new PromptContextBuilder()),
            definition);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hi");
        await runtime.WaitUntilIdleAsync();

        for (var i = 0; i < 3; i++)
        {
            if (i > 0)
            {
                time.Advance(TimeSpan.FromSeconds(5));
                await runtime.WaitUntilMailboxDrainedAsync();
            }

            await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
            await runtime.WaitUntilIdleAsync();
            Assert.Equal(i + 1, CountStarted(output, "LongSilence"));
        }
    }

    [Fact]
    public async Task Runtime_caps_long_silence_even_when_brain_always_returns_speak()
    {
        var time = Clock();
        var output = new CapturingSessionOutput();
        var definition = RepeatPolicy(maxPerSilence: 5, consecutiveCap: 2);
        var brain = new AlwaysSpeakLongSilenceBrain();
        await using var runtime = Create(
            output,
            new ScriptedLanguageModel(["Hello from synthetic.", "One.", "Two.", "Three."]),
            time,
            brain,
            definition);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hi");
        await runtime.WaitUntilIdleAsync();

        for (var i = 0; i < 4; i++)
        {
            if (i > 0)
            {
                time.Advance(TimeSpan.FromSeconds(5));
                await runtime.WaitUntilMailboxDrainedAsync();
            }

            await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
            await runtime.WaitUntilIdleAsync();
        }

        Assert.Equal(2, CountStarted(output, "LongSilence"));
        Assert.Equal(SessionStatus.Attached, runtime.Snapshot.Status);
    }

    [Fact]
    public async Task Environment_speak_is_independent_of_silence_cap()
    {
        var time = Clock();
        var output = new CapturingSessionOutput();
        var definition = SampleDefinitions.Support with
        {
            InitiativePolicy = new InitiativePolicy(
                true,
                10000,
                5000,
                1,
                ["longSilence", "environmentUpdate", "unfinishedInteraction"],
                MaxConsecutiveProactiveTurns: 1)
        };
        var brain = new ScriptedProactiveBrain(definition);
        await using var runtime = Create(
            output,
            new ScriptedLanguageModel(["Need a hint?", "Order moved."]),
            time,
            brain,
            definition);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello?");
        await runtime.WaitUntilIdleAsync();
        await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(1, CountStarted(output, "LongSilence"));
        await runtime.SubmitEnvironmentAsync(
            SyntheticEnvironmentDriver.OrderShipped(Guid.Parse("019944af-0000-7000-8000-0000000000aa"), "D-9"));
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(1, CountStarted(output, "EnvironmentUpdate"));
        Assert.Equal(SessionStatus.Attached, runtime.Snapshot.Status);
    }

    private static int CountStarted(CapturingSessionOutput output, string trigger) =>
        output.Items.Count(item => item.Payload is ResponseStartedOutput started && started.Trigger == trigger);

    private static FakeTimeProvider Clock() =>
        new(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));

    private static AgentDefinition RepeatPolicy(int maxPerSilence, int consecutiveCap, int? silentEvaluations = null) =>
        SampleDefinitions.Examiner with
        {
            InitiativePolicy = new InitiativePolicy(
                true,
                8000,
                5000,
                maxPerSilence,
                ["longSilence"],
                MaxConsecutiveProactiveTurns: consecutiveCap,
                MaxSilentEvaluations: silentEvaluations)
        };

    private static SessionRuntime Create(
        ISessionOutput output,
        ILanguageModel model,
        FakeTimeProvider time,
        IAgentBrain brain,
        AgentDefinition? definition = null,
        IInterruptionClassifier? classifier = null,
        IAttachmentStore? attachments = null)
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
            classifier,
            attachments: attachments);
    }

    private sealed class StaySilentBrain : IAgentBrain
    {
        public ValueTask<AgentDecision> DecideAsync(
            AgentContext context,
            Guid responseId,
            CancellationToken cancellationToken = default)
        {
            if (context.Trigger.Kind == TriggerKind.UserTurn)
            {
                return ValueTask.FromResult<AgentDecision>(new Speak(new PromptContextBuilder().Build(context, responseId)));
            }

            if (context.SilentEvaluations >= context.Definition.InitiativePolicy.SilentEvaluationCap
                || context.InactivityExceeded)
            {
                return ValueTask.FromResult<AgentDecision>(new RequestDeactivate("Silent bound."));
            }

            return ValueTask.FromResult<AgentDecision>(new StaySilent("scripted"));
        }
    }

    private sealed class ScriptedProactiveBrain(AgentDefinition definition) : IAgentBrain
    {
        public int Calls { get; private set; }

        public ValueTask<AgentDecision> DecideAsync(
            AgentContext context,
            Guid responseId,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            var builder = new PromptContextBuilder();
            if (context.Trigger.Kind == TriggerKind.UserTurn)
            {
                return ValueTask.FromResult<AgentDecision>(new Speak(builder.Build(context, responseId)));
            }

            if (context.Trigger.Kind == TriggerKind.LongSilence)
            {
                if (context.SilentEvaluations >= definition.InitiativePolicy.SilentEvaluationCap
                    || context.InactivityExceeded)
                {
                    return ValueTask.FromResult<AgentDecision>(new RequestDeactivate("Cap reached."));
                }

                if (definition.InitiativePolicy.ConsecutiveCap > 0
                    && context.ConsecutiveProactiveSpeaks >= definition.InitiativePolicy.ConsecutiveCap)
                {
                    return ValueTask.FromResult<AgentDecision>(new StaySilent("Consecutive proactive cap reached."));
                }

                return ValueTask.FromResult<AgentDecision>(new Speak(builder.Build(context, responseId)));
            }

            if (context.Trigger.Kind is TriggerKind.EnvironmentUpdate or TriggerKind.UnfinishedInteraction)
            {
                return ValueTask.FromResult<AgentDecision>(new Speak(builder.Build(context, responseId)));
            }

            return ValueTask.FromResult<AgentDecision>(new StaySilent("scripted"));
        }
    }

    private sealed class AlwaysSpeakLongSilenceBrain : IAgentBrain
    {
        public ValueTask<AgentDecision> DecideAsync(
            AgentContext context,
            Guid responseId,
            CancellationToken cancellationToken = default)
        {
            var builder = new PromptContextBuilder();
            return context.Trigger.Kind switch
            {
                TriggerKind.UserTurn => ValueTask.FromResult<AgentDecision>(new Speak(builder.Build(context, responseId))),
                TriggerKind.LongSilence => ValueTask.FromResult<AgentDecision>(new Speak(builder.Build(context, responseId))),
                _ => ValueTask.FromResult<AgentDecision>(new StaySilent("scripted"))
            };
        }
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
