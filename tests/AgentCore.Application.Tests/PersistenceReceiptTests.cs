using AgentCore.Application.Agents;
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

public sealed class PersistenceReceiptTests
{
    [Fact]
    public async Task User_turn_does_not_start_generation_until_save_acknowledges()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new GatedMemoryStore(gate);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        var brain = new RecordingAgentBrain(new DefaultAgentBrain(new PromptContextBuilder()));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 64).Select(index => Guid.Parse($"019944af-0000-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842")]);
        var now = time.GetUtcNow();
        var snapshot = new SessionSnapshot(
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
        await store.SaveAsync(snapshot, 0);
        await using var runtime = new SessionRuntime(
            snapshot,
            new ScriptedLanguageModel(),
            brain,
            store,
            new CapturingSessionOutput(),
            ids,
            time,
            NullLogger<SessionRuntime>.Instance);
        await runtime.AttachAsync();
        await runtime.WaitUntilMailboxDrainedAsync();
        var pending = runtime.SubmitUserTextAsync("Hello");
        await store.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, brain.Calls);
        gate.TrySetResult();
        await pending;
        await runtime.WaitUntilIdleAsync();
        Assert.True(brain.Calls >= 1);
    }

    [Fact]
    public async Task Failed_terminal_end_save_does_not_ack_ended_status()
    {
        await using var harness = await SqliteTestHarness.CreateMigratedAsync();
        var store = new FailingEndStore(harness.Store);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 64).Select(index => Guid.Parse($"019944af-0000-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842")]);
        var now = time.GetUtcNow();
        var snapshot = new SessionSnapshot(
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
        await harness.Store.SaveAsync(snapshot, 0);
        await using var runtime = new SessionRuntime(
            snapshot,
            new ScriptedLanguageModel(),
            new DefaultAgentBrain(new PromptContextBuilder()),
            store,
            new CapturingSessionOutput(),
            ids,
            time,
            NullLogger<SessionRuntime>.Instance);
        await runtime.AttachAsync();
        await runtime.WaitUntilMailboxDrainedAsync();
        var ending = runtime.RequestEndAsync();
        await PumpUntilAsync(time, ending);
        await runtime.WaitUntilMailboxDrainedAsync();
        var loaded = await harness.Store.LoadAsync(snapshot.SessionId);
        Assert.NotEqual(SessionStatus.Ended, loaded!.Status);
        Assert.True(store.Failed);
    }

    [Fact]
    public async Task Failed_later_pause_save_cannot_revert_ended_status()
    {
        var inner = new InMemoryMemoryStore();
        var store = new PauseAfterEndStore(inner);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 64).Select(index => Guid.Parse($"019944af-0000-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842")]);
        var now = time.GetUtcNow();
        var snapshot = new SessionSnapshot(
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
        await inner.SaveAsync(snapshot, 0);
        await using var runtime = new SessionRuntime(
            snapshot,
            new ScriptedLanguageModel(),
            new DefaultAgentBrain(new PromptContextBuilder()),
            store,
            new CapturingSessionOutput(),
            ids,
            time,
            NullLogger<SessionRuntime>.Instance);
        await runtime.AttachAsync();
        await runtime.WaitUntilMailboxDrainedAsync();
        Assert.True(await runtime.RequestEndAsync());
        await runtime.DetachAsync();
        await runtime.WaitUntilMailboxDrainedAsync();
        var loaded = await inner.LoadAsync(snapshot.SessionId);
        Assert.Equal(SessionStatus.Ended, loaded!.Status);
        Assert.Equal(0, store.PauseAfterEnd);
    }

    private static async Task PumpUntilAsync(FakeTimeProvider time, Task task)
    {
        for (var attempt = 0; attempt < 50 && !task.IsCompleted; attempt++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(5);
        }

        await task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class GatedMemoryStore(TaskCompletionSource gate) : IMemoryStore
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
            if (snapshot.Entries.Count > 0)
            {
                SaveStarted.TrySetResult();
                await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            await _inner.SaveAsync(snapshot, expectedRevision, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask<IReadOnlyList<ConversationEntry>> ReadHistoryAsync(
            Guid sessionId,
            long afterEntrySequence,
            int limit,
            CancellationToken cancellationToken = default) =>
            _inner.ReadHistoryAsync(sessionId, afterEntrySequence, limit, cancellationToken);

        public ValueTask<UserProfile?> LoadProfileAsync(
            Guid profileId,
            CancellationToken cancellationToken = default) =>
            _inner.LoadProfileAsync(profileId, cancellationToken);

        public ValueTask SaveProfileAsync(
            UserProfile profile,
            long expectedRevision,
            CancellationToken cancellationToken = default) =>
            _inner.SaveProfileAsync(profile, expectedRevision, cancellationToken);

        public ValueTask RecoverCrashedSessionsAsync(CancellationToken cancellationToken = default) =>
            _inner.RecoverCrashedSessionsAsync(cancellationToken);
    }

    private sealed class FailingEndStore(IMemoryStore inner) : IMemoryStore
    {
        public bool Failed { get; private set; }

        public ValueTask<SessionSnapshot?> LoadAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            inner.LoadAsync(sessionId, cancellationToken);

        public ValueTask SaveAsync(SessionSnapshot snapshot, long expectedRevision, CancellationToken cancellationToken = default)
        {
            if (snapshot.Status == SessionStatus.Ended)
            {
                Failed = true;
                throw AgentCoreErrors.Persistence("forced end save failure");
            }

            return inner.SaveAsync(snapshot, expectedRevision, cancellationToken);
        }

        public ValueTask<IReadOnlyList<ConversationEntry>> ReadHistoryAsync(
            Guid sessionId,
            long afterEntrySequence,
            int limit,
            CancellationToken cancellationToken = default) =>
            inner.ReadHistoryAsync(sessionId, afterEntrySequence, limit, cancellationToken);

        public ValueTask<UserProfile?> LoadProfileAsync(Guid profileId, CancellationToken cancellationToken = default) =>
            inner.LoadProfileAsync(profileId, cancellationToken);

        public ValueTask SaveProfileAsync(UserProfile profile, long expectedRevision, CancellationToken cancellationToken = default) =>
            inner.SaveProfileAsync(profile, expectedRevision, cancellationToken);

        public ValueTask RecoverCrashedSessionsAsync(CancellationToken cancellationToken = default) =>
            inner.RecoverCrashedSessionsAsync(cancellationToken);
    }

    private sealed class PauseAfterEndStore(IMemoryStore inner) : IMemoryStore
    {
        private bool _ended;

        public int PauseAfterEnd { get; private set; }

        public ValueTask<SessionSnapshot?> LoadAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            inner.LoadAsync(sessionId, cancellationToken);

        public ValueTask SaveAsync(SessionSnapshot snapshot, long expectedRevision, CancellationToken cancellationToken = default)
        {
            if (snapshot.Status == SessionStatus.Ended)
            {
                _ended = true;
            }

            if (snapshot.Status == SessionStatus.Paused && _ended)
            {
                PauseAfterEnd++;
                throw AgentCoreErrors.Persistence("forced pause after end");
            }

            return inner.SaveAsync(snapshot, expectedRevision, cancellationToken);
        }

        public ValueTask<IReadOnlyList<ConversationEntry>> ReadHistoryAsync(
            Guid sessionId,
            long afterEntrySequence,
            int limit,
            CancellationToken cancellationToken = default) =>
            inner.ReadHistoryAsync(sessionId, afterEntrySequence, limit, cancellationToken);

        public ValueTask<UserProfile?> LoadProfileAsync(Guid profileId, CancellationToken cancellationToken = default) =>
            inner.LoadProfileAsync(profileId, cancellationToken);

        public ValueTask SaveProfileAsync(
            UserProfile profile,
            long expectedRevision,
            CancellationToken cancellationToken = default) =>
            inner.SaveProfileAsync(profile, expectedRevision, cancellationToken);

        public ValueTask RecoverCrashedSessionsAsync(CancellationToken cancellationToken = default) =>
            inner.RecoverCrashedSessionsAsync(cancellationToken);
    }
}
