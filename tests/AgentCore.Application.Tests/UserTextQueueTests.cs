using AgentCore.Application.Agents;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Domain.Conversation;
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
        Assert.DoesNotContain(harness.Output.TextDeltas, delta => delta.Text == "R1b");
        Assert.Equal(["Hello", "queued later"], UserTexts(harness));
        Assert.DoesNotContain(harness.Output.Items, item => item.Payload is ResponseCompletedOutput);

        harness.Model.Gate.TrySetResult();
        await harness.Runtime.WaitUntilIdleAsync();
        Assert.Contains(harness.Output.TextDeltas, delta => delta.Text == "R1b");
        Assert.Equal(1, harness.Model.Calls);
        Assert.Equal(r1, harness.Snapshot.Entries.Single(entry => entry.Role == ConversationRole.Assistant).ResponseId);
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
        SessionMode mode)
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
            SampleDefinitions.Examiner,
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

    private sealed record Harness(SessionRuntime Runtime, CapturingSessionOutput Output, GatedThenLiveModel Model)
    {
        public SessionSnapshot Snapshot => Runtime.Snapshot;
    }
}

file sealed class HoldingLanguageModel : ILanguageModel
{
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ModelCapabilities Capabilities { get; } = new(true, true);

    public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
        ModelRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var call = Interlocked.Increment(ref _calls);
        yield return new ModelTextDelta($"T{call}");
        await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        yield return new ModelCompleted(ModelStopReason.Completed);
    }

    private int _calls;
}
