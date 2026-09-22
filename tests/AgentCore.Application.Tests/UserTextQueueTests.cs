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

public sealed class UserTextQueueTests
{
    [Fact]
    public async Task Queue_during_live_response_persists_without_superseding()
    {
        var harness = await LiveGeneratingAsync();
        var r1 = harness.Runtime.ActiveResponseId;
        Assert.NotNull(r1);

        await harness.Runtime.SubmitPersistedUserTextAsync(
            "queued later",
            Guid.NewGuid(),
            CancellationToken.None,
            null,
            UserTextBehavior.Queue);

        Assert.Equal(r1, harness.Runtime.ActiveResponseId);
        Assert.Equal(1, harness.Model.Calls);
        Assert.Equal(["Hello", "queued later"], UserTexts(harness));
        Assert.DoesNotContain(harness.Output.Items, item => item.Payload is ResponseCompletedOutput);

        harness.Model.Gate.TrySetResult();
        await harness.Runtime.WaitUntilIdleAsync();
        Assert.Contains(harness.Output.TextDeltas, delta => delta.Text == "R1b");
        Assert.Equal(2, harness.Model.Calls);
        Assert.Contains(harness.Output.TextDeltas, delta => delta.Text == "R2");
        Assert.Equal(2, harness.Snapshot.Entries.Count(entry => entry.Role == ConversationRole.Assistant));
        await harness.Runtime.DisposeAsync();
    }

    [Fact]
    public async Task Interrupt_after_queued_user_keeps_queue_earlier_in_history()
    {
        var harness = await LiveGeneratingAsync();
        var r1 = harness.Runtime.ActiveResponseId;

        await harness.Runtime.SubmitPersistedUserTextAsync(
            "queued later",
            Guid.NewGuid(),
            CancellationToken.None,
            null,
            UserTextBehavior.Queue);
        await harness.Runtime.SubmitPersistedUserTextAsync(
            "take over",
            Guid.NewGuid(),
            CancellationToken.None,
            null,
            UserTextBehavior.Interrupt);
        await harness.Output.WaitForAsync(item => item.Payload is TextDeltaOutput delta && delta.Text == "R2");

        Assert.NotEqual(r1, harness.Runtime.ActiveResponseId);
        Assert.Equal(["Hello", "queued later", "take over"], UserTexts(harness));
        Assert.DoesNotContain(harness.Output.TextDeltas, delta => delta.Text == "R1b");
        Assert.Contains(
            harness.Output.Items,
            item => item.ResponseId == r1
                && item.Payload is ResponseCompletedOutput completed
                && completed.InterruptReason == "userSteer");

        harness.Model.Gate.TrySetResult();
        await harness.Runtime.WaitUntilIdleAsync();
        Assert.DoesNotContain(harness.Output.TextDeltas, delta => delta.Text == "R1b");
        Assert.Contains(harness.Output.TextDeltas, delta => delta.Text == "R2");
        await harness.Runtime.DisposeAsync();
    }

    [Fact]
    public async Task Omitted_behavior_interrupts_like_interrupt()
    {
        var harness = await LiveGeneratingAsync();
        var r1 = harness.Runtime.ActiveResponseId;
        await harness.Runtime.SubmitPersistedUserTextAsync("stop that", Guid.NewGuid());
        await harness.Output.WaitForAsync(item => item.Payload is TextDeltaOutput delta && delta.Text == "R2");
        Assert.NotEqual(r1, harness.Runtime.ActiveResponseId);
        harness.Model.Gate.TrySetResult();
        await harness.Runtime.WaitUntilIdleAsync();
        Assert.Contains(harness.Output.TextDeltas, delta => delta.Text == "R2");
        await harness.Runtime.DisposeAsync();
    }

    [Fact]
    public async Task Queue_while_idle_still_starts_a_turn()
    {
        var time = Clock();
        var output = new CapturingSessionOutput();
        var model = new ScriptedLanguageModel();
        var brain = new RecordingAgentBrain(new DefaultAgentBrain(new PromptContextBuilder()));
        await using var runtime = CreateRuntime(output, model, time, brain, new FakeInterruptionClassifier(), SessionMode.Text);
        await runtime.AttachAsync();
        await runtime.SubmitPersistedUserTextAsync(
            "Hello",
            Guid.NewGuid(),
            CancellationToken.None,
            null,
            UserTextBehavior.Queue);
        await runtime.WaitUntilIdleAsync();
        Assert.Contains(output.Items, item => item.Payload is ResponseCompletedOutput);
        Assert.Equal("Hello", runtime.Snapshot.Title);
    }

    [Fact]
    public async Task Interrupt_terminalizes_before_persistence_completes_and_starts_R2_after()
    {
        var persistGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new GatedPersistStore(persistGate);
        var time = Clock();
        var output = new CapturingSessionOutput();
        var model = new HoldingLanguageModel();
        var runtime = CreateRuntime(
            output,
            model,
            time,
            new RecordingAgentBrain(new DefaultAgentBrain(new PromptContextBuilder())),
            new FakeInterruptionClassifier(),
            SessionMode.Text,
            store: store);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello");
        await output.WaitForAsync(item => item.Payload is TextDeltaOutput delta && delta.Text == "T1");
        var r1 = runtime.ActiveResponseId;
        Assert.NotNull(r1);

        var u2Event = Guid.NewGuid();
        var persistTask = runtime.SubmitPersistedUserTextAsync(
            "take over",
            u2Event,
            CancellationToken.None,
            null,
            UserTextBehavior.Interrupt);

        await store.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(runtime.ActiveResponseId);
        Assert.Contains(
            output.Items,
            item => item.ResponseId == r1
                && item.Payload is ResponseCompletedOutput completed
                && completed.InterruptReason == "userSteer");
        Assert.Equal(1, model.Calls);
        Assert.DoesNotContain(output.Items, item => item.Payload is TextDeltaOutput delta && delta.Text == "T2");

        persistGate.TrySetResult();
        Assert.True(await persistTask);
        await output.WaitForAsync(item => item.Payload is TextDeltaOutput delta && delta.Text == "T2");
        Assert.Equal(2, model.Calls);

        model.Release.TrySetResult();
        await runtime.WaitUntilIdleAsync();
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Queue_while_response_active_before_started_event_does_not_interrupt()
    {
        var inner = new CapturingSessionOutput();
        var releaseStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new DeferredResponseStartedOutput(inner, releaseStarted);
        var model = new HoldingLanguageModel();
        var time = Clock();
        var runtime = CreateRuntime(
            output,
            model,
            time,
            new RecordingAgentBrain(new DefaultAgentBrain(new PromptContextBuilder())),
            new FakeInterruptionClassifier(),
            SessionMode.Text);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("first");
        await WaitForActiveResponseAsync(runtime);

        var r1 = runtime.ActiveResponseId;
        Assert.NotNull(r1);
        Assert.DoesNotContain(inner.Items, item => item.Payload is ResponseStartedOutput);

        Assert.True(await runtime.SubmitPersistedUserTextAsync(
            "second",
            Guid.NewGuid(),
            CancellationToken.None,
            null,
            UserTextBehavior.Queue));

        Assert.Equal(r1, runtime.ActiveResponseId);
        Assert.DoesNotContain(
            inner.Items,
            item => item.ResponseId == r1 && item.Payload is ResponseCompletedOutput);

        releaseStarted.TrySetResult();
        await inner.WaitForAsync(item => item.Payload is ResponseStartedOutput);

        model.Release.TrySetResult();
        await runtime.WaitUntilIdleAsync();

        Assert.DoesNotContain(
            inner.Items,
            item => item.ResponseId == r1
                && item.Payload is ResponseCompletedOutput completed
                && completed.InterruptReason is not null);
        Assert.Contains(inner.TextDeltas, delta => delta.Text == "T2");
        Assert.Equal(["first", "second"], UserTextsFromSnapshot(runtime));
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Detach_while_response_generating_records_disconnected_reason()
    {
        var output = new CapturingSessionOutput();
        var model = new HoldingLanguageModel();
        var time = Clock();
        var runtime = CreateRuntime(
            output,
            model,
            time,
            new RecordingAgentBrain(new DefaultAgentBrain(new PromptContextBuilder())),
            new FakeInterruptionClassifier(),
            SessionMode.Text);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello");
        await WaitForActiveResponseAsync(runtime);
        var r1 = runtime.ActiveResponseId;
        Assert.NotNull(r1);

        await runtime.DetachAsync();

        Assert.Contains(
            output.Items,
            item => item.ResponseId == r1
                && item.Payload is ResponseCompletedOutput completed
                && completed.InterruptReason == "disconnected");
        model.Release.TrySetResult();
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task CancelResponse_is_idempotent_stale_and_unknown()
    {
        var time = Clock();
        var output = new CapturingSessionOutput();
        var model = new HoldingLanguageModel();
        var brain = new RecordingAgentBrain(new DefaultAgentBrain(new PromptContextBuilder()));
        var runtime = CreateRuntime(output, model, time, brain, new FakeInterruptionClassifier(), SessionMode.Text);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello");
        await output.WaitForAsync(item => item.Payload is TextDeltaOutput delta && delta.Text == "T1");
        var r1 = runtime.ActiveResponseId!.Value;

        await runtime.SubmitPersistedUserTextAsync("next", Guid.NewGuid());
        await output.WaitForAsync(item => item.Payload is TextDeltaOutput delta && delta.Text == "T2");
        var r2 = runtime.ActiveResponseId;
        Assert.NotNull(r2);
        Assert.NotEqual(r1, r2);

        Assert.Equal(ResponseCancelResult.Stale, await runtime.CancelResponseAsync(r1));
        Assert.Equal(r2, runtime.ActiveResponseId);

        Assert.Equal(ResponseCancelResult.Cancelled, await runtime.CancelResponseAsync(r2.Value));
        await runtime.WaitUntilMailboxDrainedAsync();
        Assert.Null(runtime.ActiveResponseId);
        Assert.Equal(ResponseCancelResult.Idempotent, await runtime.CancelResponseAsync(r2.Value));
        Assert.Equal(
            ResponseCancelResult.Unknown,
            await runtime.CancelResponseAsync(Guid.Parse("019944af-ffff-7000-8000-000000000099")));

        model.Release.TrySetResult();
        await runtime.WaitUntilIdleAsync();
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task CancelResponse_does_not_append_a_user_entry()
    {
        var harness = await LiveGeneratingAsync();
        var r1 = harness.Runtime.ActiveResponseId!.Value;
        Assert.Equal(ResponseCancelResult.Cancelled, await harness.Runtime.CancelResponseAsync(r1));
        await harness.Runtime.WaitUntilMailboxDrainedAsync();
        Assert.Equal(["Hello"], UserTexts(harness));
        harness.Model.Gate.TrySetResult();
        await harness.Runtime.WaitUntilIdleAsync();
        await harness.Runtime.DisposeAsync();
    }

    [Fact]
    public async Task Two_queued_users_become_one_next_response_and_stay_distinct()
    {
        var time = Clock();
        var output = new CapturingSessionOutput();
        var model = new GatedThenLiveModel();
        var recorded = new RecordingAgentBrain(new DefaultAgentBrain(new PromptContextBuilder()));
        var runtime = CreateRuntime(output, model, time, recorded, new FakeInterruptionClassifier(), SessionMode.Text);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello");
        await output.WaitForAsync(item => item.Payload is TextDeltaOutput delta && delta.Text == "R1a");
        await runtime.SubmitPersistedUserTextAsync("U2", Guid.NewGuid(), CancellationToken.None, null, UserTextBehavior.Queue);
        await runtime.SubmitPersistedUserTextAsync("U3", Guid.NewGuid(), CancellationToken.None, null, UserTextBehavior.Queue);
        Assert.Equal(1, model.Calls);
        Assert.Equal(["Hello", "U2", "U3"], runtime.Snapshot.Entries.Where(entry => entry.Role == ConversationRole.User).Select(entry => entry.Text).ToArray());
        model.Gate.TrySetResult();
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(2, model.Calls);
        Assert.Equal(2, runtime.Snapshot.Entries.Count(entry => entry.Role == ConversationRole.Assistant));
        Assert.Equal(["Hello", "U2", "U3"], runtime.Snapshot.Entries.Where(entry => entry.Role == ConversationRole.User).Select(entry => entry.Text).ToArray());
        var second = recorded.Contexts.Last(context => context.Trigger.Kind == TriggerKind.UserTurn);
        Assert.Equal(["Hello", "U2", "U3"], second.History.Where(entry => entry.Role == ConversationRole.User).Select(entry => entry.Text).ToArray());
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Stop_does_not_start_trailing_user_suffix()
    {
        var time = Clock();
        var output = new CapturingSessionOutput();
        var model = new HoldingLanguageModel();
        var runtime = CreateRuntime(
            output,
            model,
            time,
            new RecordingAgentBrain(new DefaultAgentBrain(new PromptContextBuilder())),
            new FakeInterruptionClassifier(),
            SessionMode.Text);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello");
        await output.WaitForAsync(item => item.Payload is TextDeltaOutput delta && delta.Text == "T1");
        var r1 = runtime.ActiveResponseId!.Value;
        await runtime.SubmitPersistedUserTextAsync("U2", Guid.NewGuid(), CancellationToken.None, null, UserTextBehavior.Queue);
        await runtime.SubmitPersistedUserTextAsync("U3", Guid.NewGuid(), CancellationToken.None, null, UserTextBehavior.Queue);
        Assert.Equal(ResponseCancelResult.Cancelled, await runtime.CancelResponseAsync(r1));
        await Task.Delay(250);
        Assert.Equal(1, model.Calls);
        Assert.Null(runtime.ActiveResponseId);
        model.Release.TrySetResult();
        await runtime.WaitUntilIdleAsync();
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Attach_recovers_trailing_suffix_as_one_response_without_duplicating_users()
    {
        var time = Clock();
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 256).Select(index => Guid.Parse($"019944af-0000-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842")]);
        var store = new InMemoryMemoryStore();
        var now = time.GetUtcNow();
        var sessionId = ids.NewSessionId();
        ConversationEntry User(long seq, string text, string key) => new(
            Guid.Parse($"019944af-ffff-7000-8000-{key}"),
            seq,
            Guid.Parse($"019944af-ffff-7000-8000-{key}"),
            ConversationRole.User,
            text,
            null,
            EntryStatus.Completed,
            SessionMode.Text,
            text.Length,
            text.Length,
            now);
        var assistant = new ConversationEntry(
            Guid.Parse("019944af-ffff-7000-8000-0000000000a1"),
            2,
            null,
            ConversationRole.Assistant,
            "done",
            Guid.Parse("019944af-ffff-7000-8000-0000000000a0"),
            EntryStatus.Completed,
            SessionMode.Text,
            4,
            4,
            now);
        var snapshot = new SessionSnapshot(
            1,
            sessionId,
            1,
            SampleDefinitions.Examiner,
            SessionMode.Text,
            null,
            SessionStatus.Created,
            [User(1, "U1", "000000000001"), assistant, User(3, "U2", "000000000003"), User(4, "U3", "000000000004")],
            string.Empty,
            0,
            null,
            null,
            now,
            now);
        await store.SaveAsync(snapshot, 0);
        var output = new CapturingSessionOutput();
        var recorded = new RecordingAgentBrain(new DefaultAgentBrain(new PromptContextBuilder()));
        await using var runtime = new SessionRuntime(
            snapshot,
            new ScriptedLanguageModel(["batch"]),
            recorded,
            store,
            output,
            ids,
            time,
            NullLogger<SessionRuntime>.Instance,
            new FakeInterruptionClassifier());
        await runtime.AttachAsync();
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(["U1", "U2", "U3"], runtime.Snapshot.Entries.Where(entry => entry.Role == ConversationRole.User).Select(entry => entry.Text).ToArray());
        Assert.Equal(2, runtime.Snapshot.Entries.Count(entry => entry.Role == ConversationRole.Assistant));
        Assert.Contains(recorded.Contexts, context =>
            context.Trigger.Kind == TriggerKind.UserTurn
            && context.History.Count(entry => entry.Role == ConversationRole.User && entry.Text is "U2" or "U3") == 2);
    }

    [Fact]
    public async Task Attach_does_not_rebatch_when_assistant_already_started_for_the_suffix()
    {
        var time = Clock();
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 256).Select(index => Guid.Parse($"019944af-0000-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842")]);
        var store = new InMemoryMemoryStore();
        var now = time.GetUtcNow();
        var sessionId = ids.NewSessionId();
        ConversationEntry User(long seq, string text, string key) => new(
            Guid.Parse($"019944af-ffff-7000-8000-{key}"),
            seq,
            Guid.Parse($"019944af-ffff-7000-8000-{key}"),
            ConversationRole.User,
            text,
            null,
            EntryStatus.Completed,
            SessionMode.Text,
            text.Length,
            text.Length,
            now);
        var started = new ConversationEntry(
            Guid.Parse("019944af-ffff-7000-8000-0000000000b1"),
            3,
            null,
            ConversationRole.Assistant,
            "partial",
            Guid.Parse("019944af-ffff-7000-8000-0000000000b0"),
            EntryStatus.Interrupted,
            SessionMode.Text,
            7,
            7,
            now);
        var snapshot = new SessionSnapshot(
            1,
            sessionId,
            1,
            SampleDefinitions.Examiner,
            SessionMode.Text,
            null,
            SessionStatus.Created,
            [User(1, "U2", "000000000011"), User(2, "U3", "000000000012"), started],
            string.Empty,
            0,
            null,
            null,
            now,
            now);
        await store.SaveAsync(snapshot, 0);
        var output = new CapturingSessionOutput();
        var model = new ScriptedLanguageModel(["should-not-run"]);
        await using var runtime = new SessionRuntime(
            snapshot,
            model,
            new RecordingAgentBrain(new DefaultAgentBrain(new PromptContextBuilder())),
            store,
            output,
            ids,
            time,
            NullLogger<SessionRuntime>.Instance,
            new FakeInterruptionClassifier());
        await runtime.AttachAsync();
        await runtime.WaitUntilMailboxDrainedAsync();
        Assert.Equal(1, runtime.Snapshot.Entries.Count(entry => entry.Role == ConversationRole.Assistant));
        Assert.DoesNotContain(output.TextDeltas, delta => delta.Text == "should-not-run");
    }

    [Fact]
    public async Task Pending_suffix_blocks_long_silence_inactivity_pause_and_environment()
    {
        var time = Clock();
        var output = new CapturingSessionOutput();
        var model = new HoldingLanguageModel();
        var recorded = new RecordingAgentBrain(new DefaultAgentBrain(new PromptContextBuilder()));
        var definition = SampleDefinitions.Support with
        {
            InitiativePolicy = SampleDefinitions.Support.InitiativePolicy with { MaxInactivityMs = 5_000 }
        };
        var runtime = CreateRuntime(
            output,
            model,
            time,
            recorded,
            new FakeInterruptionClassifier(),
            SessionMode.Text,
            definition);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello");
        await output.WaitForAsync(item => item.Payload is TextDeltaOutput delta && delta.Text == "T1");
        var generation = runtime.TimerGeneration;
        await runtime.SubmitPersistedUserTextAsync("U2", Guid.NewGuid(), CancellationToken.None, null, UserTextBehavior.Queue);
        time.Advance(TimeSpan.FromSeconds(10));
        await runtime.SubmitTimerElapsedAsync("idle", generation);
        await runtime.WaitUntilMailboxDrainedAsync();
        Assert.Equal(SessionStatus.Attached, runtime.Snapshot.Status);
        Assert.Equal(0, recorded.Triggers.Count(kind => kind == TriggerKind.LongSilence));
        await runtime.SubmitEnvironmentAsync(SyntheticEnvironmentDriver.OrderShipped(Guid.NewGuid(), "A-1"));
        await runtime.WaitUntilMailboxDrainedAsync();
        Assert.Equal(0, recorded.Triggers.Count(kind => kind == TriggerKind.EnvironmentUpdate));
        Assert.Equal(1, model.Calls);
        model.Release.TrySetResult();
        await runtime.WaitUntilIdleAsync();
        Assert.Contains(recorded.Triggers, kind => kind == TriggerKind.UserTurn);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Healthy_response_failure_still_advances_the_pending_suffix_once()
    {
        var time = Clock();
        var output = new CapturingSessionOutput();
        var model = new FailThenSpeakModel();
        var runtime = CreateRuntime(
            output,
            model,
            time,
            new RecordingAgentBrain(new DefaultAgentBrain(new PromptContextBuilder())),
            new FakeInterruptionClassifier(),
            SessionMode.Text);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello");
        await output.WaitForAsync(item => item.Payload is TextDeltaOutput delta && delta.Text == "fail");
        await runtime.SubmitPersistedUserTextAsync("U2", Guid.NewGuid(), CancellationToken.None, null, UserTextBehavior.Queue);
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(2, model.Calls);
        Assert.Equal(1, output.Items.Count(item => item.Payload is ResponseCompletedOutput completed && completed.Failed));
        Assert.Contains(output.TextDeltas, delta => delta.Text == "ok");
        Assert.Equal(2, runtime.Snapshot.Entries.Count(entry => entry.Role == ConversationRole.Assistant));
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Lifecycle_resume_dispatches_pending_queued_suffix_once()
    {
        var time = Clock();
        var output = new CapturingSessionOutput();
        var model = new HoldingLanguageModel();
        var runtime = CreateRuntime(
            output,
            model,
            time,
            new RecordingAgentBrain(new DefaultAgentBrain(new PromptContextBuilder())),
            new FakeInterruptionClassifier(),
            SessionMode.Text);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello");
        await output.WaitForAsync(item => item.Payload is TextDeltaOutput delta && delta.Text == "T1");
        await runtime.SubmitPersistedUserTextAsync("U2", Guid.NewGuid(), CancellationToken.None, null, UserTextBehavior.Queue);
        Assert.True(await runtime.RequestLifecycleTransitionAsync(
            SessionLifecycleStatus.Paused,
            LifecycleTransitionSource.User,
            "manual"));
        await runtime.WaitUntilMailboxDrainedAsync();
        Assert.Equal(1, model.Calls);
        Assert.True(await runtime.RequestLifecycleTransitionAsync(
            SessionLifecycleStatus.Active,
            LifecycleTransitionSource.User,
            "resume"));
        await output.WaitForAsync(item => item.Payload is TextDeltaOutput delta && delta.Text == "T2");
        Assert.Equal(2, model.Calls);
        model.Release.TrySetResult();
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(2, model.Calls);
        Assert.Contains(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant && entry.Text.Contains("T1", StringComparison.Ordinal));
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Pause_does_not_start_the_pending_suffix()
    {
        var time = Clock();
        var output = new CapturingSessionOutput();
        var model = new HoldingLanguageModel();
        var runtime = CreateRuntime(
            output,
            model,
            time,
            new RecordingAgentBrain(new DefaultAgentBrain(new PromptContextBuilder())),
            new FakeInterruptionClassifier(),
            SessionMode.Text);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello");
        await output.WaitForAsync(item => item.Payload is TextDeltaOutput delta && delta.Text == "T1");
        await runtime.SubmitPersistedUserTextAsync("U2", Guid.NewGuid(), CancellationToken.None, null, UserTextBehavior.Queue);
        Assert.True(await runtime.RequestDeactivateAsync());
        await runtime.WaitUntilMailboxDrainedAsync();
        Assert.Equal(SessionStatus.Paused, runtime.Snapshot.Status);
        Assert.Equal(1, model.Calls);
        Assert.Contains(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.User && entry.Text == "U2");
        model.Release.TrySetResult();
        await runtime.WaitUntilMailboxDrainedAsync();
        Assert.Equal(1, model.Calls);
        await runtime.DisposeAsync();
    }

    private static string[] UserTextsFromSnapshot(SessionRuntime runtime) =>
        runtime.Snapshot.Entries.Where(entry => entry.Role == ConversationRole.User).Select(entry => entry.Text).ToArray();

    private static async Task WaitForActiveResponseAsync(SessionRuntime runtime)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (runtime.ActiveResponseId is not null)
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("Active response was not established.");
    }

    private static string[] UserTexts(Harness harness) =>
        harness.Snapshot.Entries.Where(entry => entry.Role == ConversationRole.User).Select(entry => entry.Text).ToArray();

    private static async Task<Harness> LiveGeneratingAsync()
    {
        var time = Clock();
        var output = new CapturingSessionOutput();
        var model = new GatedThenLiveModel();
        var brain = new RecordingAgentBrain(new DefaultAgentBrain(new PromptContextBuilder()));
        var classifier = new FakeInterruptionClassifier();
        var runtime = CreateRuntime(output, model, time, brain, classifier, SessionMode.Text);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello");
        await output.WaitForAsync(item => item.Payload is TextDeltaOutput delta && delta.Text == "R1a");
        return new Harness(runtime, output, model);
    }

    private static FakeTimeProvider Clock() =>
        new(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));

    private static SessionRuntime CreateRuntime(
        ISessionOutput output,
        ILanguageModel model,
        FakeTimeProvider time,
        IAgentBrain brain,
        IInterruptionClassifier classifier,
        SessionMode mode,
        AgentDefinition? definition = null,
        IMemoryStore? store = null)
    {
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 256).Select(index => Guid.Parse($"019944af-0000-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842")]);
        var memory = store ?? new InMemoryMemoryStore();
        var now = time.GetUtcNow();
        var snapshot = new SessionSnapshot(
            1,
            ids.NewSessionId(),
            1,
            definition ?? SampleDefinitions.Examiner,
            mode,
            null,
            SessionStatus.Created,
            [],
            string.Empty,
            0,
            null,
            null,
            now,
            now);
        memory.SaveAsync(snapshot, 0).AsTask().GetAwaiter().GetResult();
        return new SessionRuntime(
            snapshot,
            model,
            brain,
            memory,
            output,
            ids,
            time,
            NullLogger<SessionRuntime>.Instance,
            classifier);
    }

    private sealed record Harness(SessionRuntime Runtime, CapturingSessionOutput Output, GatedThenLiveModel Model)
    {
        public SessionSnapshot Snapshot => Runtime.Snapshot;
    }
}

file sealed class DeferredResponseStartedOutput(CapturingSessionOutput inner, TaskCompletionSource releaseStarted) : ISessionOutput
{
    private readonly List<SessionOutput> _held = [];
    private int _releaseScheduled;

    public ValueTask PublishAsync(SessionOutput output, CancellationToken cancellationToken = default)
    {
        if (output.Payload is ResponseStartedOutput)
        {
            lock (_held)
            {
                _held.Add(output);
            }

            if (Interlocked.Exchange(ref _releaseScheduled, 1) == 0)
            {
                _ = FlushHeldAsync();
            }

            return ValueTask.CompletedTask;
        }

        return inner.PublishAsync(output, cancellationToken);
    }

    private async Task FlushHeldAsync()
    {
        await releaseStarted.Task.ConfigureAwait(false);
        List<SessionOutput> batch;
        lock (_held)
        {
            batch = [.. _held];
            _held.Clear();
        }

        foreach (var item in batch)
        {
            await inner.PublishAsync(item).ConfigureAwait(false);
        }
    }
}

file sealed class HoldingLanguageModel : ILanguageModel
{
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Calls { get; private set; }

    public ModelCapabilities Capabilities { get; } = new(true, true);

    public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
        ModelRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var call = Interlocked.Increment(ref _calls);
        Calls = call;
        yield return new ModelTextDelta($"T{call}");
        await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        yield return new ModelCompleted(ModelStopReason.Completed);
    }

    private int _calls;
}

file sealed class GatedPersistStore(TaskCompletionSource persistGate) : IMemoryStore
{
    private readonly InMemoryMemoryStore _inner = new();
    public TaskCompletionSource SaveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask<SessionSnapshot?> LoadAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        _inner.LoadAsync(sessionId, cancellationToken);

    public async ValueTask SaveAsync(
        SessionSnapshot snapshot,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        if (snapshot.Entries.Any(entry => entry.Role == ConversationRole.User && entry.Text == "take over"))
        {
            SaveStarted.TrySetResult();
            await persistGate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        await _inner.SaveAsync(snapshot, expectedRevision, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<IReadOnlyList<ConversationEntry>> ReadHistoryAsync(
        Guid sessionId,
        long afterEntrySequence,
        int limit,
        CancellationToken cancellationToken = default) =>
        _inner.ReadHistoryAsync(sessionId, afterEntrySequence, limit, cancellationToken);

    public ValueTask<ConversationHistoryPage?> ReadHistoryPageAsync(
        Guid sessionId,
        long? afterEntrySequence,
        long? beforeEntrySequence,
        int limit,
        CancellationToken cancellationToken = default) =>
        _inner.ReadHistoryPageAsync(sessionId, afterEntrySequence, beforeEntrySequence, limit, cancellationToken);

    public ValueTask<UserProfile?> LoadProfileAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        _inner.LoadProfileAsync(profileId, cancellationToken);

    public ValueTask SaveProfileAsync(UserProfile profile, long expectedRevision, CancellationToken cancellationToken = default) =>
        _inner.SaveProfileAsync(profile, expectedRevision, cancellationToken);

    public ValueTask RecoverCrashedSessionsAsync(CancellationToken cancellationToken = default) =>
        _inner.RecoverCrashedSessionsAsync(cancellationToken);
}

file sealed class FailThenSpeakModel : ILanguageModel
{
    public int Calls { get; private set; }

    public ModelCapabilities Capabilities { get; } = new(true, true);

    public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
        ModelRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var call = Interlocked.Increment(ref _calls);
        Calls = call;
        if (call == 1)
        {
            yield return new ModelTextDelta("fail");
            yield return new ModelFailed(new ProviderFailure(ProviderErrorCode.Unavailable, "synthetic failure"));
            yield break;
        }

        yield return new ModelTextDelta("ok");
        yield return new ModelCompleted(ModelStopReason.Completed);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private int _calls;
}
