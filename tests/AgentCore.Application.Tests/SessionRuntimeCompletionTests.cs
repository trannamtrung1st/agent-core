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

public sealed class SessionRuntimeCompletionTests
{
    [Fact]
    public async Task Ongoing_session_does_not_evaluate_completion_after_a_turn()
    {
        var counter = new CountingLanguageModel(new ScriptedLanguageModel());
        var output = new CapturingSessionOutput();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 19, 6, 0, 0, TimeSpan.Zero));
        await using var runtime = Create(output, time, counter);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello");
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(SessionLifecycleStatus.Active, runtime.Snapshot.LifecycleStatus);
        Assert.Equal(1, counter.Calls);
        Assert.DoesNotContain(output.Items, item => item.Payload is CompletionIntentOutput);
    }

    [Fact]
    public async Task Continue_on_a_configured_goal_leaves_the_session_active()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 19, 6, 0, 0, TimeSpan.Zero));
        var output = new CapturingSessionOutput();
        await using var runtime = Create(
            output,
            time,
            new ScriptedLanguageModel(completionDecision: """{"decision":"continue","reason":"still working"}"""),
            snapshot: GoalSnapshot(time.GetUtcNow(), AgentCompletionAuthority.Allowed));
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello");
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(SessionLifecycleStatus.Active, runtime.Snapshot.LifecycleStatus);
        Assert.Equal(SessionStatus.Attached, runtime.Snapshot.Status);
        Assert.Contains(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant);
    }

    [Fact]
    public async Task Advisory_requestComplete_publishes_intent_and_stays_active()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 19, 6, 0, 0, TimeSpan.Zero));
        var output = new CapturingSessionOutput();
        await using var runtime = Create(
            output,
            time,
            new ScriptedLanguageModel(completionDecision: """{"decision":"requestComplete","reason":"candidate finished"}"""),
            snapshot: GoalSnapshot(time.GetUtcNow(), AgentCompletionAuthority.Advisory));
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello");
        await output.WaitForAsync(item => item.Payload is CompletionIntentOutput);
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(SessionLifecycleStatus.Active, runtime.Snapshot.LifecycleStatus);
        var intent = Assert.IsType<CompletionIntentOutput>(
            output.Items.Select(item => item.Payload).OfType<CompletionIntentOutput>().Last());
        Assert.True(intent.Advisory);
        Assert.Equal("candidate finished", intent.Reason);
    }

    [Fact]
    public async Task Allowed_requestComplete_waits_until_the_final_answer_terminalizes()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 19, 6, 0, 0, TimeSpan.Zero));
        var output = new CapturingSessionOutput();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var runtime = Create(
            output,
            time,
            new ScriptedLanguageModel(
                ["First ", "closing."],
                release,
                completionDecision: """{"decision":"requestComplete","reason":"goal met"}"""),
            snapshot: GoalSnapshot(time.GetUtcNow(), AgentCompletionAuthority.Allowed));
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Please finish.");
        await output.WaitForAsync(item => item.Payload is TextDeltaOutput);
        Assert.Equal(SessionLifecycleStatus.Active, runtime.Snapshot.LifecycleStatus);
        Assert.NotNull(runtime.ActiveResponseId);
        release.TrySetResult();
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(SessionLifecycleStatus.Completed, runtime.Snapshot.LifecycleStatus);
        Assert.Equal(SessionStatus.Ended, runtime.Snapshot.Status);
        var assistant = runtime.Snapshot.Entries.Single(entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal(EntryStatus.Completed, assistant.Status);
    }

    [Fact]
    public async Task Malformed_completion_evaluation_leaves_the_session_active()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 19, 6, 0, 0, TimeSpan.Zero));
        var output = new CapturingSessionOutput();
        await using var runtime = Create(
            output,
            time,
            new ScriptedLanguageModel(completionDecision: "???"),
            snapshot: GoalSnapshot(time.GetUtcNow(), AgentCompletionAuthority.Allowed));
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello");
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(SessionLifecycleStatus.Active, runtime.Snapshot.LifecycleStatus);
    }

    [Fact]
    public async Task Host_completion_does_not_call_the_language_model()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 19, 6, 0, 0, TimeSpan.Zero));
        var store = new InMemoryMemoryStore();
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 16).Select(index => Guid.Parse($"019944af-0003-7000-8000-{index:D12}")),
            Enumerable.Range(1, 8).Select(index => Guid.Parse($"873f07d1-e264-4c81-a31b-7e59e940b8{index:D2}")).ToArray());
        var manager = new SessionManager(
            new StaticDefinitions(SampleDefinitions.Examiner),
            store,
            ids,
            time,
            new VoiceAvailability { SpeechAdaptersResolved = true });
        var created = await manager.CreateAsync(
            "examiner",
            null,
            SessionMode.Text,
            purpose: new SessionPurpose(SessionPurposeKind.Goal, "Host closes"),
            policy: new SessionCompletionPolicy(AgentCompletionAuthority.Allowed, true, true));
        var completed = await manager.TransitionLifecycleAsync(
            created.SessionId,
            SessionLifecycleStatus.Completed,
            LifecycleTransitionSource.Host,
            reason: "host accepted");
        Assert.Equal(SessionLifecycleStatus.Completed, completed.LifecycleStatus);
        Assert.Equal("host accepted", completed.LifecycleReason);
    }

    private static SessionSnapshot GoalSnapshot(DateTimeOffset now, AgentCompletionAuthority authority) =>
        new(
            1,
            Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842"),
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
            Purpose: new SessionPurpose(SessionPurposeKind.Goal, "Collect a spoken sample"),
            CompletionPolicy: new SessionCompletionPolicy(authority, true, true));

    private static SessionRuntime Create(
        ISessionOutput output,
        FakeTimeProvider time,
        ILanguageModel model,
        IMemoryStore? store = null,
        SessionSnapshot? snapshot = null)
    {
        store ??= new InMemoryMemoryStore();
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 64).Select(index => Guid.Parse($"019944af-0000-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842")]);
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
            now);
        if (store.LoadAsync(snapshot.SessionId).AsTask().GetAwaiter().GetResult() is null)
        {
            store.SaveAsync(snapshot, 0).AsTask().GetAwaiter().GetResult();
        }

        return new SessionRuntime(
            snapshot,
            model,
            new DefaultAgentBrain(new PromptContextBuilder()),
            store,
            output,
            ids,
            time,
            NullLogger<SessionRuntime>.Instance,
            policy: new InteractionPolicy(PendingVoiceTimeoutMs: 30_000),
            voice: new VoiceAvailability { SpeechAdaptersResolved = true });
    }

    private sealed class CountingLanguageModel(ILanguageModel inner) : ILanguageModel
    {
        public int Calls { get; private set; }

        public ModelCapabilities Capabilities => inner.Capabilities;

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            await foreach (var item in inner.GenerateAsync(request, cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }
        }
    }

    private sealed class StaticDefinitions(AgentDefinition definition) : IAgentDefinitionStore
    {
        public ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AgentDefinition>>([definition]);

        public ValueTask<AgentDefinition?> GetAsync(
            string id,
            int? version = null,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<AgentDefinition?>(
                string.Equals(id, definition.Id, StringComparison.Ordinal) ? definition : null);
    }
}
