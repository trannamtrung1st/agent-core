using System.Diagnostics.Metrics;
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
public sealed class InitiativeTests
{
    [Fact]
    public async Task Idle_offers_one_long_silence_hint_per_silence_period()
    {
        var time = Clock();
        var output = new CapturingSessionOutput();
        var model = new ScriptedLanguageModel(["Need a hint?"]);
        var brain = RecordingDefaultBrain(model);
        await using var runtime = Create(output, model, time, brain);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello?");
        await runtime.WaitUntilIdleAsync();
        var started = CountStarted(output, "LongSilence");
        time.Advance(TimeSpan.FromSeconds(91));
        await runtime.WaitUntilMailboxDrainedAsync();
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
        var model = new QueuedInitiativeLanguageModel(
            [
                """{"decision":"speak","intent":"hint","objective":"First hint."}""",
                """{"decision":"speak","intent":"hint","objective":"Second hint."}"""
            ],
            ["Still there?", "Follow up?", "Third?"]);
        var brain = RecordingDefaultBrain(model);
        await using var runtime = Create(
            output,
            model,
            time,
            brain,
            definition);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello?");
        await runtime.WaitUntilIdleAsync();
        time.Advance(TimeSpan.FromSeconds(91));
        await runtime.WaitUntilMailboxDrainedAsync();
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
        var model = new ScriptedLanguageModel();
        var brain = new RecordingAgentBrain(new StaySilentBrain());
        await using var runtime = Create(output, model, time, brain);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hi");
        await runtime.WaitUntilIdleAsync();
        var calls = brain.Calls;
        await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(0, CountStarted(output, "LongSilence"));
        Assert.Equal(OutputActivity.Idle, runtime.Output);
        Assert.True(brain.Calls > calls);
        calls = brain.Calls;
        await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
        await runtime.WaitUntilMailboxDrainedAsync();
        Assert.Equal(calls, brain.Calls);
        time.Advance(TimeSpan.FromSeconds(31));
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
        var model = new ScriptedLanguageModel(["Need a hint?"]);
        var inner = RecordingDefaultBrain(model);
        var brain = new GatedInitiativeBrain(inner, gate);
        await using var runtime = Create(output, model, time, brain);
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
        var brain = RecordingDefaultBrain(new ScriptedLanguageModel());
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
        var brain = RecordingDefaultBrain(new ScriptedLanguageModel());
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
        var model = new ScriptedLanguageModel();
        var brain = RecordingDefaultBrain(model);
        await using var runtime = Create(output, model, time, brain, SampleDefinitions.Support);
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
        var model = new ScriptedLanguageModel(["Still there?"]);
        var brain = RecordingDefaultBrain(model);
        await using var runtime = Create(output, model, time, brain, SampleDefinitions.Support);
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
                time.Advance(TimeSpan.FromSeconds(31));
                await runtime.WaitUntilMailboxDrainedAsync();
            }

            await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
            await runtime.WaitUntilIdleAsync();
            Assert.Equal(i + 1, CountStarted(output, "LongSilence"));
        }

        time.Advance(TimeSpan.FromSeconds(31));
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
        var model = new ScriptedLanguageModel();
        await using var runtime = Create(
            output,
            model,
            time,
            new DefaultAgentBrain(new PromptContextBuilder(), new DefaultInitiativeEvaluator(new PromptContextBuilder(), model)),
            definition);
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
        time.Advance(TimeSpan.FromSeconds(31));
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
        time.Advance(TimeSpan.FromSeconds(31));
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
        await runtime.SubmitUserTextAsync("Hi");
        await runtime.WaitUntilIdleAsync();
        await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(0, CountStarted(output, "LongSilence"));
        Assert.Equal(SessionStatus.Attached, runtime.Snapshot.Status);
        time.Advance(TimeSpan.FromSeconds(31));
        await runtime.WaitUntilMailboxDrainedAsync();
        await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(SessionStatus.Attached, runtime.Snapshot.Status);
        time.Advance(TimeSpan.FromSeconds(31));
        await runtime.WaitUntilMailboxDrainedAsync();
        await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(SessionStatus.Paused, runtime.Snapshot.Status);
        Assert.Equal("silentEvaluation", runtime.Snapshot.PauseReason);
        Assert.NotEqual(SessionStatus.Ended, runtime.Snapshot.Status);
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
        time.Advance(TimeSpan.FromSeconds(31));
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
        var model = new QueuedInitiativeLanguageModel(
            [
                """{"decision":"speak","intent":"followUp","objective":"First follow-up."}""",
                """{"decision":"speak","intent":"followUp","objective":"Second follow-up."}"""
            ],
            ["Hello from synthetic.", "Following up without a question mark."]);
        var brain = RecordingDefaultBrain(model);
        var definition = RepeatPolicy(maxPerSilence: 5, consecutiveCap: 5);
        await using var runtime = Create(
            output,
            model,
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

        time.Advance(TimeSpan.FromSeconds(31));
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
        var model = new QueuedInitiativeLanguageModel(
            [
                """{"decision":"speak","intent":"other","objective":"Proactive one."}""",
                """{"decision":"speak","intent":"other","objective":"Proactive two."}""",
                """{"decision":"speak","intent":"other","objective":"Proactive three."}"""
            ],
            ["Hello from synthetic.", "Proactive two.", "Proactive three.", "Proactive four."]);
        await using var runtime = Create(
            output,
            model,
            time,
            new DefaultAgentBrain(new PromptContextBuilder(), new DefaultInitiativeEvaluator(new PromptContextBuilder(), model)),
            definition);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hi");
        await runtime.WaitUntilIdleAsync();

        for (var i = 0; i < 3; i++)
        {
            if (i > 0)
            {
                time.Advance(TimeSpan.FromSeconds(31));
                await runtime.WaitUntilMailboxDrainedAsync();
            }

            await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
            await runtime.WaitUntilIdleAsync();
            Assert.Equal(i + 1, CountStarted(output, "LongSilence"));
        }
    }

    [Fact]
    public async Task Initiative_evaluation_can_stay_silent_while_caps_still_allow_another_speak()
    {
        var time = Clock();
        var output = new CapturingSessionOutput();
        var definition = RepeatPolicy(maxPerSilence: 5, consecutiveCap: 5);
        var model = new QueuedInitiativeLanguageModel(
            [
                """{"decision":"speak","intent":"hint","objective":"First useful nudge."}""",
                """{"decision":"staySilent","reason":"Nothing new to add.","nextWaitMs":120000}"""
            ],
            ["Hello from synthetic.", "Would have followed up."]);
        var brain = RecordingDefaultBrain(model);
        await using var runtime = Create(output, model, time, brain, definition);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hi");
        await runtime.WaitUntilIdleAsync();
        await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(1, CountStarted(output, "LongSilence"));
        time.Advance(TimeSpan.FromSeconds(31));
        await runtime.WaitUntilMailboxDrainedAsync();
        await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(1, CountStarted(output, "LongSilence"));
        Assert.Equal(SessionStatus.Attached, runtime.Snapshot.Status);
        Assert.Equal(TimeSpan.FromSeconds(120), runtime.LastArmedIdleDelay);
    }

    [Fact]
    public async Task Stay_silent_next_wait_inside_bounds_is_armed()
    {
        var captured = CaptureWaitMetrics();
        using var listener = captured.Listener;
        var time = Clock();
        var output = new CapturingSessionOutput();
        var definition = RepeatPolicy(maxPerSilence: 5, consecutiveCap: 5);
        var model = new QueuedInitiativeLanguageModel(
            ["""{"decision":"staySilent","reason":"Wait a bit.","nextWaitMs":45000}"""],
            ["Hello from synthetic."]);
        await using var runtime = Create(output, model, time, RecordingDefaultBrain(model), definition);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hi");
        await runtime.WaitUntilIdleAsync();
        await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(TimeSpan.FromSeconds(45), runtime.LastArmedIdleDelay);
        Assert.Contains(
            captured.Samples,
            sample => sample.Value == 45_000 && sample.Source == "model" && sample.Clamp == "none");
    }

    [Fact]
    public async Task Stay_silent_too_small_and_too_large_waits_are_clamped()
    {
        var captured = CaptureWaitMetrics();
        using var listener = captured.Listener;
        var time = Clock();
        var output = new CapturingSessionOutput();
        var definition = RepeatPolicy(maxPerSilence: 5, consecutiveCap: 5);
        var model = new QueuedInitiativeLanguageModel(
            [
                """{"decision":"staySilent","reason":"Too small.","nextWaitMs":1000}""",
                """{"decision":"staySilent","reason":"Too large.","nextWaitMs":3600000}"""
            ],
            ["Hello from synthetic."]);
        await using var runtime = Create(output, model, time, RecordingDefaultBrain(model), definition);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hi");
        await runtime.WaitUntilIdleAsync();
        await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(TimeSpan.FromSeconds(30), runtime.LastArmedIdleDelay);
        time.Advance(TimeSpan.FromSeconds(31));
        await runtime.WaitUntilMailboxDrainedAsync();
        await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(TimeSpan.FromMilliseconds(450_000), runtime.LastArmedIdleDelay);
        Assert.Contains(captured.Samples, sample => sample.Value == 30_000 && sample.Clamp == "min");
        Assert.Contains(captured.Samples, sample => sample.Value == 450_000 && sample.Clamp == "max");
    }

    [Fact]
    public async Task Null_next_wait_uses_deterministic_idle_backoff()
    {
        var captured = CaptureWaitMetrics();
        using var listener = captured.Listener;
        var time = Clock();
        var output = new CapturingSessionOutput();
        var definition = RepeatPolicy(maxPerSilence: 5, consecutiveCap: 5);
        var model = new QueuedInitiativeLanguageModel(
            ["""{"decision":"staySilent","reason":"No wait supplied."}"""],
            ["Hello from synthetic."]);
        await using var runtime = Create(output, model, time, RecordingDefaultBrain(model), definition);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hi");
        await runtime.WaitUntilIdleAsync();
        await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(TimeSpan.FromSeconds(30), runtime.LastArmedIdleDelay);
        Assert.Contains(
            captured.Samples,
            sample => sample.Source == "fallback" && sample.Clamp == "n/a" && sample.Value == 30_000);
    }

    [Fact]
    public async Task Deactivate_ignores_planner_next_wait_and_does_not_arm_idle()
    {
        var captured = CaptureWaitMetrics();
        using var listener = captured.Listener;
        var time = Clock();
        var output = new CapturingSessionOutput();
        var definition = RepeatPolicy(maxPerSilence: 5, consecutiveCap: 5);
        var model = new QueuedInitiativeLanguageModel(
            ["""{"decision":"deactivate","reason":"Pause now.","nextWaitMs":120000}"""],
            ["Hello from synthetic."]);
        await using var runtime = Create(output, model, time, RecordingDefaultBrain(model), definition);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hi");
        await runtime.WaitUntilIdleAsync();
        await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(SessionStatus.Paused, runtime.Snapshot.Status);
        Assert.Equal("initiative", runtime.Snapshot.PauseReason);
        Assert.Null(runtime.LastArmedIdleDelay);
        var started = CountStarted(output, "LongSilence");
        time.Advance(TimeSpan.FromSeconds(180));
        await runtime.WaitUntilMailboxDrainedAsync();
        await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
        await runtime.WaitUntilMailboxDrainedAsync();
        Assert.Equal(started, CountStarted(output, "LongSilence"));
        Assert.Contains(captured.Samples, sample => sample.Source == "none" && sample.Value == 0);
        Assert.DoesNotContain(
            captured.Samples,
            sample => sample.Source == "model" && sample.Value == 120_000);
    }

    [Fact]
    public async Task Proactive_speak_next_wait_is_armed_after_the_response_completes()
    {
        var time = Clock();
        var output = new CapturingSessionOutput();
        var definition = RepeatPolicy(maxPerSilence: 5, consecutiveCap: 5);
        var model = new QueuedInitiativeLanguageModel(
            ["""{"decision":"speak","intent":"hint","objective":"Offer a short hint.","nextWaitMs":45000}"""],
            ["Hello from synthetic.", "Here is a hint."]);
        await using var runtime = Create(output, model, time, RecordingDefaultBrain(model), definition);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hi");
        await runtime.WaitUntilIdleAsync();
        await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(1, CountStarted(output, "LongSilence"));
        Assert.Equal(TimeSpan.FromSeconds(45), runtime.LastArmedIdleDelay);
    }

    [Fact]
    public async Task Pending_initiative_wait_is_interrupted_by_user_input()
    {
        var time = Clock();
        var output = new CapturingSessionOutput();
        var definition = RepeatPolicy(maxPerSilence: 5, consecutiveCap: 5);
        var model = new QueuedInitiativeLanguageModel(
            ["""{"decision":"staySilent","reason":"Waiting.","nextWaitMs":120000}"""],
            ["Hello from synthetic.", "User returned."]);
        await using var runtime = Create(output, model, time, RecordingDefaultBrain(model), definition);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hi");
        await runtime.WaitUntilIdleAsync();
        await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
        await runtime.WaitUntilIdleAsync();
        var generation = runtime.TimerGeneration;
        await runtime.SubmitUserTextAsync("I am here");
        await runtime.WaitUntilIdleAsync();
        Assert.NotEqual(generation, runtime.TimerGeneration);
        Assert.Equal(0, CountStarted(output, "LongSilence"));
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
                time.Advance(TimeSpan.FromSeconds(31));
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

    private static WaitMetricCapture CaptureWaitMetrics()
    {
        var samples = new List<WaitSample>();
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == RuntimeTelemetry.Name
                && instrument.Name == InitiativeWaitTelemetry.InstrumentName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, state) =>
        {
            string? source = null;
            string? clamp = null;
            string? mode = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "source")
                {
                    source = tag.Value?.ToString();
                }
                else if (tag.Key == "clamp")
                {
                    clamp = tag.Value?.ToString();
                }
                else if (tag.Key == "mode")
                {
                    mode = tag.Value?.ToString();
                }
            }

            samples.Add(new WaitSample(value, source, clamp, mode));
        });
        listener.Start();
        return new WaitMetricCapture(listener, samples);
    }

    private sealed record WaitMetricCapture(MeterListener Listener, List<WaitSample> Samples);

    private sealed record WaitSample(double Value, string? Source, string? Clamp, string? Mode);

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

    private static RecordingAgentBrain RecordingDefaultBrain(ILanguageModel model) =>
        new(new DefaultAgentBrain(
            new PromptContextBuilder(),
            new DefaultInitiativeEvaluator(new PromptContextBuilder(), model)));

    [Fact]
    public async Task Detach_after_inactivity_pause_preserves_pause_reason()
    {
        var time = Clock();
        var output = new CapturingSessionOutput();
        var definition = SampleDefinitions.Examiner with
        {
            InitiativePolicy = new InitiativePolicy(
                true,
                8000,
                5000,
                1,
                ["longSilence"],
                MaxConsecutiveProactiveTurns: 1,
                MaxInactivityMs: 1_000)
        };
        var model = new ScriptedLanguageModel();
        var brain = RecordingDefaultBrain(model);
        await using var runtime = Create(output, model, time, brain, definition);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello");
        await runtime.WaitUntilIdleAsync();
        time.Advance(TimeSpan.FromSeconds(10));
        await runtime.WaitUntilMailboxDrainedAsync();
        await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(SessionStatus.Paused, runtime.Snapshot.Status);
        Assert.Equal("inactivity", runtime.Snapshot.PauseReason);

        await runtime.DetachAsync();
        Assert.Equal(SessionStatus.Paused, runtime.Snapshot.Status);
        Assert.Equal("inactivity", runtime.Snapshot.PauseReason);
    }

    [Fact]
    public async Task Semantic_initiative_stay_silent_prevents_repeat_nudges_after_first_reply()
    {
        var time = Clock();
        var output = new CapturingSessionOutput();
        var model = new QueuedInitiativeLanguageModel(
            [
                """{"decision":"staySilent","reason":"User only said hi; no new help needed."}""",
                """{"decision":"staySilent","reason":"Still nothing concrete to add."}"""
            ],
            ["Hello! How can I help you today?"]);
        var brain = RecordingDefaultBrain(model);
        await using var runtime = Create(output, model, time, brain);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("hi");
        await runtime.WaitUntilIdleAsync();
        time.Advance(TimeSpan.FromSeconds(31));
        await runtime.WaitUntilMailboxDrainedAsync();
        await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
        await runtime.WaitUntilIdleAsync();
        time.Advance(TimeSpan.FromSeconds(31));
        await runtime.WaitUntilMailboxDrainedAsync();
        await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(0, CountStarted(output, "LongSilence"));
    }

    [Fact]
    public async Task Explicit_reopen_resets_inactivity_after_pause()
    {
        var time = Clock();
        var output = new CapturingSessionOutput();
        var definition = SampleDefinitions.Examiner with
        {
            InitiativePolicy = new InitiativePolicy(
                true,
                8000,
                5000,
                1,
                ["longSilence"],
                MaxInactivityMs: 30_000)
        };
        var model = new ScriptedLanguageModel();
        var brain = RecordingDefaultBrain(model);
        await using var runtime = Create(output, model, time, brain, definition);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello");
        await runtime.WaitUntilIdleAsync();
        time.Advance(TimeSpan.FromSeconds(35));
        await runtime.WaitUntilMailboxDrainedAsync();
        await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(SessionStatus.Paused, runtime.Snapshot.Status);
        Assert.Equal("inactivity", runtime.Snapshot.PauseReason);

        var now = time.GetUtcNow();
        var reopened = runtime.Snapshot with
        {
            Status = SessionStatus.Created,
            PauseReason = null,
            LastUserActivityAt = now,
            RuntimeEpoch = runtime.Snapshot.RuntimeEpoch + 1,
            UpdatedAt = now
        };
        await runtime.ApplyReopenedSnapshotAsync(reopened);
        await runtime.AttachAsync();
        time.Advance(TimeSpan.FromSeconds(20));
        await runtime.WaitUntilMailboxDrainedAsync();
        await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
        await runtime.WaitUntilMailboxDrainedAsync();
        Assert.Equal(SessionStatus.Attached, runtime.Snapshot.Status);
    }

    [Fact]
    public async Task Examiner_initiative_waits_after_question_then_speaks_once()
    {
        var time = Clock();
        var output = new CapturingSessionOutput();
        var model = new ScriptedLanguageModel(["Would you like to try again?"]);
        var brain = RecordingDefaultBrain(model);
        await using var runtime = Create(output, model, time, brain);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello?");
        await runtime.WaitUntilIdleAsync();
        time.Advance(TimeSpan.FromSeconds(91));
        await runtime.WaitUntilMailboxDrainedAsync();
        await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(1, CountStarted(output, "LongSilence"));
        time.Advance(TimeSpan.FromSeconds(91));
        await runtime.WaitUntilMailboxDrainedAsync();
        await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
        await runtime.WaitUntilMailboxDrainedAsync();
        Assert.Equal(1, CountStarted(output, "LongSilence"));
    }

    [Fact]
    public async Task Semantic_initiative_speak_when_evaluator_returns_speak()
    {
        var time = Clock();
        var output = new CapturingSessionOutput();
        var model = new QueuedInitiativeLanguageModel(
            ["""{"decision":"speak","intent":"followUp","objective":"Order delay still unresolved."}"""],
            ["I can check the simulated tracking details for you."]);
        var brain = RecordingDefaultBrain(model);
        var definition = RepeatPolicy(maxPerSilence: 2, consecutiveCap: 2);
        await using var runtime = Create(output, model, time, brain, definition);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("My order is late.");
        await runtime.WaitUntilIdleAsync();
        time.Advance(TimeSpan.FromSeconds(31));
        await runtime.WaitUntilMailboxDrainedAsync();
        await runtime.SubmitTimerElapsedAsync("idle", runtime.TimerGeneration);
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(1, CountStarted(output, "LongSilence"));
    }

    private sealed class QueuedInitiativeLanguageModel(IReadOnlyList<string> initiativeJson, IReadOnlyList<string> generationChunks)
        : ILanguageModel
    {
        private readonly ScriptedLanguageModel _generation = new(generationChunks);
        private int _initiativeCalls;

        public ModelCapabilities Capabilities => _generation.Capabilities;

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var system = request.Messages.FirstOrDefault(message => message.Role == ModelRole.System)?.Text;
            if (system is not null && system.Contains(InitiativeEvaluator.Marker, StringComparison.Ordinal))
            {
                var index = Math.Min(_initiativeCalls++, initiativeJson.Count - 1);
                yield return new ModelTextDelta(initiativeJson[index]);
                yield return new ModelCompleted(ModelStopReason.Completed);
                yield break;
            }

            await foreach (var evt in _generation.GenerateAsync(request, cancellationToken).ConfigureAwait(false))
            {
                yield return evt;
            }
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
