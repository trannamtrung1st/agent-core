using AgentCore.Application.Agents;
using AgentCore.Application.Events;
using AgentCore.Application.Interaction;
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

public sealed class InteractionControllerTests
{
    [Fact]
    public void Mhm_is_a_backchannel_and_wait_is_an_interrupt()
    {
        Assert.True(HeuristicPhrases.IsBackchannel("mhm"));
        Assert.True(HeuristicPhrases.IsExplicitInterrupt("Wait, what did you mean?"));
        Assert.False(HeuristicPhrases.IsBackchannel("wait"));
    }

    [Fact]
    public async Task Mhm_during_live_output_continues_without_a_user_turn()
    {
        var harness = await LiveGeneratingAsync();
        var utterance = Guid.Parse("019944af-0000-7000-8000-0000000000aa");
        await harness.Runtime.SubmitSpeechAsync(new SpeechStarted(utterance), 0.9);
        harness.Time.Advance(TimeSpan.FromMilliseconds(400));
        await harness.Runtime.SubmitSpeechAsync(new SpeechFinal(utterance, "mhm", 0.9), 0.9);
        await harness.Runtime.WaitUntilMailboxDrainedAsync();
        Assert.Equal(1, harness.Snapshot.Entries.Count(entry => entry.Role == ConversationRole.User));
        Assert.NotNull(harness.Runtime.ActiveResponseId);
        Assert.DoesNotContain(harness.Output.TextDeltas, delta => delta.Text == "R1b");
        harness.Model.Gate.TrySetResult();
        await harness.Runtime.WaitUntilIdleAsync();
        await harness.Runtime.DisposeAsync();
    }

    [Fact]
    public async Task Wait_partial_supersedes_live_response()
    {
        var harness = await LiveGeneratingAsync();
        var r1 = harness.Runtime.ActiveResponseId;
        var utterance = Guid.Parse("019944af-0000-7000-8000-0000000000ab");
        await harness.Runtime.SubmitSpeechAsync(new SpeechStarted(utterance), 0.95);
        await harness.Runtime.SubmitSpeechAsync(new SpeechPartial(utterance, 1, "wait", 0.9), 0.95);
        await harness.Runtime.WaitUntilMailboxDrainedAsync();
        Assert.Null(harness.Runtime.ActiveResponseId);
        Assert.Contains(harness.Snapshot.Entries, entry => entry.ResponseId == r1 && entry.Status == EntryStatus.Interrupted);
        harness.Model.Gate.TrySetResult();
        await harness.Runtime.WaitUntilIdleAsync();
        await harness.Runtime.DisposeAsync();
    }

    [Fact]
    public async Task Noise_is_ignored_while_model_is_gated()
    {
        var harness = await LiveGeneratingAsync();
        var utterance = Guid.Parse("019944af-0000-7000-8000-0000000000ac");
        await harness.Runtime.SubmitSpeechAsync(new SpeechStarted(utterance), 0.2);
        await harness.Runtime.WaitUntilMailboxDrainedAsync();
        Assert.Null(harness.Runtime.Candidate);
        Assert.Equal(0, harness.Classifier.Calls);
        Assert.Equal(1, harness.Brain.Calls);
        harness.Model.Gate.TrySetResult();
        await harness.Runtime.WaitUntilIdleAsync();
        await harness.Runtime.DisposeAsync();
    }

    [Fact]
    public async Task Final_before_ended_commits_once()
    {
        var time = Clock();
        var output = new CapturingSessionOutput();
        var brain = new RecordingAgentBrain(new DefaultAgentBrain(new PromptContextBuilder()));
        var classifier = new FakeInterruptionClassifier();
        await using var runtime = CreateRuntime(output, new ScriptedLanguageModel(), time, brain, classifier, SessionMode.Voice);
        await runtime.AttachAsync();
        await runtime.WaitUntilMailboxDrainedAsync();
        var utterance = Guid.Parse("019944af-0000-7000-8000-0000000000ad");
        await runtime.SubmitSpeechAsync(new SpeechStarted(utterance), 0.9);
        await runtime.SubmitSpeechAsync(new SpeechFinal(utterance, "Hello there", 0.9), 0.9);
        await runtime.SubmitSpeechAsync(new SpeechEnded(utterance), 0.9);
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(1, runtime.Snapshot.Entries.Count(entry => entry.Role == ConversationRole.User && entry.Text == "Hello there"));
        Assert.Equal(0, classifier.Calls);
    }

    [Fact]
    public async Task Stale_classifier_result_is_ignored()
    {
        var time = Clock();
        var output = new CapturingSessionOutput();
        var model = new GatedThenLiveModel();
        var brain = new RecordingAgentBrain(new DefaultAgentBrain(new PromptContextBuilder()));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var classifier = new FakeInterruptionClassifier(InteractionDecision.Interrupt, release);
        await using var runtime = CreateRuntime(output, model, time, brain, classifier, SessionMode.Voice);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello");
        await output.WaitForAsync(item => item.Payload is TextDeltaOutput delta && delta.Text == "R1a");
        var utterance = Guid.Parse("019944af-0000-7000-8000-0000000000ae");
        await runtime.SubmitSpeechAsync(new SpeechStarted(utterance), 0.9);
        await runtime.WaitUntilMailboxDrainedAsync();
        time.Advance(TimeSpan.FromMilliseconds(250));
        await runtime.SubmitSpeechAsync(new SpeechPartial(utterance, 1, "something longer", 0.4), 0.9);
        await runtime.WaitUntilMailboxDrainedAsync();
        var candidate = runtime.Candidate;
        Assert.NotNull(candidate);
        Assert.Equal(InteractionDecision.RequestInterruptionClassification, runtime.LastControllerDecision);
        Assert.Equal(1, classifier.Calls);
        await runtime.SubmitSpeechAsync(new SpeechPartial(utterance, 2, "something longer still", 0.4), 0.9);
        await runtime.WaitUntilMailboxDrainedAsync();
        var live = runtime.ActiveResponseId;
        await runtime.SubmitClassifierResultAsync(
            candidate!.CandidateId,
            utterance,
            candidate.ResponseId,
            revision: 1,
            InteractionDecision.Interrupt);
        await runtime.WaitUntilMailboxDrainedAsync();
        Assert.Equal(live, runtime.ActiveResponseId);
        release.TrySetResult();
        model.Gate.TrySetResult();
        await runtime.WaitUntilIdleAsync();
    }

    [Fact]
    public async Task Late_r1_deltas_are_discarded_after_r2_starts()
    {
        var harness = await LiveGeneratingAsync();
        var r1 = harness.Runtime.ActiveResponseId;
        await harness.Runtime.SubmitUserTextAsync("Second turn");
        await harness.Output.WaitForAsync(item => item.Payload is TextDeltaOutput delta && delta.Text == "R2");
        harness.Model.Gate.TrySetResult();
        await harness.Runtime.WaitUntilIdleAsync();
        Assert.DoesNotContain(harness.Output.TextDeltas, delta => delta.Text == "R1b");
        Assert.Contains(harness.Output.TextDeltas, delta => delta.Text == "R2");
        Assert.Contains(harness.Snapshot.Entries, entry => entry.ResponseId == r1 && entry.Status == EntryStatus.Interrupted);
        await harness.Runtime.DisposeAsync();
    }

    [Fact]
    public async Task Classifier_path_never_calls_brain_and_idle_timer_never_calls_classifier()
    {
        var harness = await LiveGeneratingAsync();
        var brainBefore = harness.Brain.Calls;
        var utterance = Guid.Parse("019944af-0000-7000-8000-0000000000af");
        await harness.Runtime.SubmitSpeechAsync(new SpeechStarted(utterance), 0.9);
        await harness.Runtime.WaitUntilMailboxDrainedAsync();
        harness.Time.Advance(TimeSpan.FromMilliseconds(250));
        await harness.Runtime.SubmitSpeechAsync(new SpeechPartial(utterance, 1, "something longer", 0.4), 0.9);
        await harness.Runtime.WaitUntilMailboxDrainedAsync();
        Assert.Equal(InteractionDecision.RequestInterruptionClassification, harness.Runtime.LastControllerDecision);
        await harness.Classifier.Called;
        Assert.Equal(brainBefore, harness.Brain.Calls);
        Assert.True(harness.Classifier.Calls >= 1);
        harness.Model.Gate.TrySetResult();
        await harness.Runtime.WaitUntilIdleAsync();
        await harness.Runtime.DisposeAsync();

        var time = Clock();
        var output = new CapturingSessionOutput();
        var brain = new RecordingAgentBrain(new DefaultAgentBrain(new PromptContextBuilder()));
        var classifier = new FakeInterruptionClassifier();
        await using var runtime = CreateRuntime(output, new ScriptedLanguageModel(), time, brain, classifier, SessionMode.Text);
        await runtime.AttachAsync();
        await runtime.WaitUntilMailboxDrainedAsync();
        var generation = runtime.TimerGeneration;
        await runtime.SubmitTimerElapsedAsync("idle", generation);
        await runtime.WaitUntilIdleAsync();
        Assert.Contains(TriggerKind.LongSilence, brain.Triggers);
        Assert.Equal(0, classifier.Calls);
    }

    [Fact]
    public async Task Invalid_timer_generation_is_ignored()
    {
        var time = Clock();
        var output = new CapturingSessionOutput();
        var brain = new RecordingAgentBrain(new DefaultAgentBrain(new PromptContextBuilder()));
        var classifier = new FakeInterruptionClassifier();
        await using var runtime = CreateRuntime(output, new ScriptedLanguageModel(), time, brain, classifier, SessionMode.Text);
        await runtime.AttachAsync();
        await runtime.WaitUntilMailboxDrainedAsync();
        var stale = runtime.TimerGeneration;
        await runtime.SubmitUserTextAsync("Hello");
        await runtime.WaitUntilIdleAsync();
        var calls = brain.Calls;
        await runtime.SubmitTimerElapsedAsync("idle", stale);
        await runtime.WaitUntilMailboxDrainedAsync();
        Assert.Equal(calls, brain.Calls);
        Assert.DoesNotContain(TriggerKind.LongSilence, brain.Triggers);
    }

    [Fact]
    public async Task Speech_and_final_fallback_interrupts_after_degraded_deadline()
    {
        var time = Clock();
        var output = new CapturingSessionOutput();
        var model = new GatedThenLiveModel();
        var brain = new RecordingAgentBrain(new DefaultAgentBrain(new PromptContextBuilder()));
        var classifier = new FakeInterruptionClassifier();
        var recognition = new RecognitionCapabilities(true, PartialTranscripts: false, true, true);
        await using var runtime = CreateRuntime(
            output,
            model,
            time,
            brain,
            classifier,
            SessionMode.Voice,
            recognition,
            new InteractionPolicy(BargeInPolicy: "semantic"));
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello");
        await output.WaitForAsync(item => item.Payload is TextDeltaOutput);
        var utterance = Guid.Parse("019944af-0000-7000-8000-0000000000b0");
        await runtime.SubmitSpeechAsync(new SpeechStarted(utterance), 0.95);
        await runtime.WaitUntilMailboxDrainedAsync();
        time.Advance(TimeSpan.FromMilliseconds(250));
        var generation = runtime.TimerGeneration;
        await runtime.SubmitTimerElapsedAsync("candidate", generation, utterance);
        await runtime.WaitUntilMailboxDrainedAsync();
        Assert.Null(runtime.ActiveResponseId);
        Assert.Equal(0, classifier.Calls);
        model.Gate.TrySetResult();
        await runtime.WaitUntilIdleAsync();
    }

    private static async Task<Harness> LiveGeneratingAsync()
    {
        var time = Clock();
        var output = new CapturingSessionOutput();
        var model = new GatedThenLiveModel();
        var brain = new RecordingAgentBrain(new DefaultAgentBrain(new PromptContextBuilder()));
        var classifier = new FakeInterruptionClassifier();
        var runtime = CreateRuntime(output, model, time, brain, classifier, SessionMode.Voice);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello");
        await output.WaitForAsync(item => item.Payload is TextDeltaOutput delta && delta.Text == "R1a");
        return new Harness(runtime, time, output, model, brain, classifier);
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
        RecognitionCapabilities? recognition = null,
        InteractionPolicy? policy = null)
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
            classifier,
            recognition,
            policy);
    }

    private sealed record Harness(
        SessionRuntime Runtime,
        FakeTimeProvider Time,
        CapturingSessionOutput Output,
        GatedThenLiveModel Model,
        RecordingAgentBrain Brain,
        FakeInterruptionClassifier Classifier)
    {
        public SessionSnapshot Snapshot => Runtime.Snapshot;
    }
}

public sealed class GatedThenLiveModel : ILanguageModel
{
    public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
            yield return new ModelTextDelta("R1a");
            await Gate.Task.ConfigureAwait(false);
            yield return new ModelTextDelta("R1b");
            yield return new ModelCompleted(ModelStopReason.Completed);
            yield break;
        }

        yield return new ModelTextDelta("R2");
        yield return new ModelCompleted(ModelStopReason.Completed);
    }

    private int _calls;
}
