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

public sealed class SessionRealtimeLifecycleTests
{
    [Fact]
    public async Task Pending_voice_times_out_to_text_without_starting_stt()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        var output = new CapturingSessionOutput();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var runtime = Create(output, time, new ScriptedLanguageModel(["AAA", "BBB"], release));

        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello");
        await output.WaitForAsync(item => item.Payload is TextDeltaOutput);
        await runtime.SetModeAsync(SessionMode.Voice);
        await runtime.WaitUntilMailboxDrainedAsync();
        Assert.Equal(SessionMode.Voice, runtime.Snapshot.PendingMode);
        Assert.Equal(SessionMode.Text, runtime.Snapshot.Mode);
        Assert.Equal(InputActivity.Idle, runtime.Input);

        var baseline = output.Items.Count;
        await Task.Yield();
        time.Advance(TimeSpan.FromMilliseconds(30_000));
        await runtime.WaitUntilMailboxDrainedAsync();
        await output.WaitForAsync(item =>
        {
            if (item.Payload is not StateChangedOutput state
                || state.PendingMode is not null
                || state.Mode != SessionMode.Text)
            {
                return false;
            }

            var items = output.Items;
            for (var index = baseline; index < items.Count; index++)
            {
                if (ReferenceEquals(items[index], item))
                {
                    return true;
                }
            }

            return false;
        });

        Assert.Null(runtime.Snapshot.PendingMode);
        Assert.Equal(SessionMode.Text, runtime.Snapshot.Mode);
        Assert.Equal(InputActivity.Idle, runtime.Input);
        release.TrySetResult();
        await runtime.WaitUntilIdleAsync();
    }

    [Fact]
    public async Task Reconnect_ready_restores_public_history_without_summary_or_live_response()
    {
        var store = new InMemoryMemoryStore();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        var first = new CapturingSessionOutput();
        await using (var runtime = Create(first, time, new ScriptedLanguageModel(), store))
        {
            await runtime.AttachAsync();
            await runtime.SubmitUserTextAsync("Hello");
            await runtime.WaitUntilIdleAsync();
            var assistant = runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant);
            Assert.Equal(0, assistant.ReceivedTextEndExclusive);
            await runtime.SubmitReceiptAsync(assistant.ResponseId!.Value, assistant.Text.Length);
            await runtime.WaitUntilMailboxDrainedAsync();
            await runtime.DetachAsync();
            await runtime.WaitUntilIdleAsync();
        }

        var second = new CapturingSessionOutput();
        var snapshot = (await store.LoadAsync(Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842")))!;
        Assert.Equal(SessionStatus.Paused, snapshot.Status);
        Assert.Null(snapshot.PendingMode);
        await using var restored = Create(second, time, new ScriptedLanguageModel(), store, snapshot);
        await restored.AttachAsync();
        await restored.WaitUntilMailboxDrainedAsync();

        var ready = Assert.IsType<ReadyOutput>(second.Items.Single(item => item.Payload is ReadyOutput).Payload);
        Assert.Null(ready.Ready.PendingMode);
        Assert.Null(ready.Ready.ActiveResponseId);
        Assert.Equal(2, ready.Ready.History.Count);
        Assert.All(ready.Ready.History, entry => Assert.Equal(SessionMode.Text, entry.DeliveryMode));
        Assert.Equal("Hello from synthetic.", ready.Ready.History[^1].Text);
        Assert.DoesNotContain(ready.Ready.History.Select(entry => entry.Text), text => text.Contains("summary", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Ready_voice_available_matches_catalog_gate_when_speech_adapters_unresolved()
    {
        var output = new CapturingSessionOutput();
        await using var runtime = Create(
            output,
            new FakeTimeProvider(DateTimeOffset.UtcNow),
            new ScriptedLanguageModel(),
            voice: new VoiceAvailability { SpeechAdaptersResolved = false });
        await runtime.AttachAsync();
        var ready = Assert.IsType<ReadyOutput>(output.Items.Single(item => item.Payload is ReadyOutput).Payload);
        Assert.False(ready.Ready.Agent.VoiceAvailable);
    }

    [Fact]
    public async Task SetMode_voice_rejected_when_speech_adapters_unresolved()
    {
        var output = new CapturingSessionOutput();
        await using var runtime = Create(
            output,
            new FakeTimeProvider(DateTimeOffset.UtcNow),
            new ScriptedLanguageModel(),
            voice: new VoiceAvailability { SpeechAdaptersResolved = false });
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await runtime.WaitUntilMailboxDrainedAsync();
        var error = Assert.IsType<ErrorOutput>(
            output.Items.Single(item => item.Payload is ErrorOutput err && err.Code == "VoiceUnavailable").Payload);
        Assert.Equal("VoiceUnavailable", error.Code);
        Assert.Equal(SessionMode.Text, runtime.Snapshot.Mode);
    }

    [Fact]
    public async Task Uncertain_source_event_retry_across_reconstruction_does_not_duplicate_user_turn()
    {
        await using var harness = await SqliteTestHarness.CreateMigratedAsync();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        var sourceEventId = Guid.Parse("019944af-0000-7000-8000-0000000000ee");
        Guid sessionId;
        await using (var runtime = Create(new CapturingSessionOutput(), time, new ScriptedLanguageModel(), harness.Store))
        {
            sessionId = runtime.SessionId;
            await runtime.AttachAsync();
            Assert.True(await runtime.SubmitUserTextAsync("Hello", sourceEventId));
            await runtime.WaitUntilIdleAsync();
            await runtime.DetachAsync();
            await runtime.WaitUntilIdleAsync();
        }

        var snapshot = (await harness.Store.LoadAsync(sessionId))!;
        await using var restored = Create(new CapturingSessionOutput(), time, new ScriptedLanguageModel(), harness.Store, snapshot);
        await restored.AttachAsync();
        await restored.WaitUntilMailboxDrainedAsync();
        Assert.True(await restored.SubmitUserTextAsync("Hello", sourceEventId));
        await restored.WaitUntilIdleAsync();
        Assert.Equal(1, restored.Snapshot.Entries.Count(entry => entry.Role == ConversationRole.User));
        Assert.Equal(sourceEventId, restored.Snapshot.Entries.Single(entry => entry.Role == ConversationRole.User).SourceEventId);
    }

    [Fact]
    public async Task Attach_and_detach_preserve_catalog_order()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero));
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
        var first = await manager.CreateAsync("examiner", 1, SessionMode.Text);
        time.Advance(TimeSpan.FromSeconds(1));
        var second = await manager.CreateAsync("examiner", 1, SessionMode.Text);
        var firstSnapshot = await manager.GetAsync(first.SessionId);

        await using var runtime = Create(new CapturingSessionOutput(), time, new ScriptedLanguageModel(), store, firstSnapshot);
        await runtime.AttachAsync();
        await runtime.WaitUntilMailboxDrainedAsync();
        await runtime.DetachAsync();
        await runtime.WaitUntilIdleAsync();

        var order = await manager.ListCatalogAsync(null, 50, false);
        Assert.Equal([second.SessionId, first.SessionId], order.Items.Select(item => item.SessionId).ToArray());
    }

    [Fact]
    public async Task Same_session_can_move_text_voice_text()
    {
        var output = new CapturingSessionOutput();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        await using var runtime = Create(output, time, new ScriptedLanguageModel());
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello");
        await runtime.WaitUntilIdleAsync();
        var sessionId = runtime.SessionId;
        await runtime.SetModeAsync(SessionMode.Voice);
        await runtime.WaitUntilMailboxDrainedAsync();
        Assert.Equal(SessionMode.Voice, runtime.Snapshot.Mode);
        await runtime.SetModeAsync(SessionMode.Text);
        await runtime.WaitUntilMailboxDrainedAsync();
        Assert.Equal(SessionMode.Text, runtime.Snapshot.Mode);
        Assert.Equal(sessionId, runtime.SessionId);
        Assert.Contains(runtime.Snapshot.Entries, entry => entry.DeliveryMode == SessionMode.Text);
    }

    private static SessionRuntime Create(
        ISessionOutput output,
        FakeTimeProvider time,
        ILanguageModel model,
        IMemoryStore? store = null,
        SessionSnapshot? snapshot = null,
        VoiceAvailability? voice = null)
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
            voice: voice ?? new VoiceAvailability { SpeechAdaptersResolved = true });
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
