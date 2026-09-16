using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class SessionManagerConcurrencyTests
{
    [Fact]
    public async Task Concurrent_first_creates_share_one_local_profile()
    {
        var inner = new InMemoryMemoryStore();
        var store = new BarrierProfileStore(inner);
        var manager = CreateManager(store);
        var first = manager.CreateAsync("examiner", null, SessionMode.Text);
        var second = manager.CreateAsync("examiner", null, SessionMode.Text);
        var created = await Task.WhenAll(first, second);
        Assert.Equal(2, created.Length);
        Assert.All(created, snapshot => Assert.Equal(LocalUserProfile.Id, snapshot.ProfileId));
        var profile = await inner.LoadProfileAsync(LocalUserProfile.Id);
        Assert.NotNull(profile);
        Assert.Equal("friend", profile!.Preferences["preferredName"]);
    }

    [Fact]
    public async Task Concurrent_first_sqlite_creates_share_one_local_profile()
    {
        await using var harness = await SqliteTestHarness.CreateMigratedAsync();
        var store = new BarrierProfileStore(harness.Store);
        var manager = CreateManager(store);
        var created = await Task.WhenAll(
            manager.CreateAsync("examiner", null, SessionMode.Text),
            manager.CreateAsync("examiner", null, SessionMode.Text));
        Assert.All(created, snapshot => Assert.Equal(LocalUserProfile.Id, snapshot.ProfileId));
        var profile = await harness.Store.LoadProfileAsync(LocalUserProfile.Id);
        Assert.NotNull(profile);
    }

    [Fact]
    public async Task End_of_already_ended_session_does_not_write_another_revision()
    {
        var store = new InMemoryMemoryStore();
        var manager = CreateManager(store);
        var created = await manager.CreateAsync("examiner", null, SessionMode.Text);
        await manager.EndAsync(created.SessionId);
        var ended = await store.LoadAsync(created.SessionId);
        Assert.Equal(SessionStatus.Ended, ended!.Status);
        var revision = ended.Revision;
        await manager.EndAsync(created.SessionId);
        var again = await store.LoadAsync(created.SessionId);
        Assert.Equal(revision, again!.Revision);
        Assert.Equal(SessionStatus.Ended, again.Status);
    }

    [Fact]
    public async Task Concurrent_end_of_the_same_session_does_not_throw_conflict()
    {
        var inner = new InMemoryMemoryStore();
        var store = new BarrierEndStore(inner);
        var manager = CreateManager(store);
        var created = await manager.CreateAsync("examiner", null, SessionMode.Text);
        await Task.WhenAll(
            manager.EndAsync(created.SessionId),
            manager.EndAsync(created.SessionId));
        var ended = await inner.LoadAsync(created.SessionId);
        Assert.Equal(SessionStatus.Ended, ended!.Status);
    }

    private static SessionManager CreateManager(IMemoryStore store)
    {
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 16).Select(index => Guid.Parse($"019944af-0003-7000-8000-{index:D12}")),
            Enumerable.Range(1, 8).Select(index => Guid.Parse($"873f07d1-e264-4c81-a31b-7e59e940b8{index:D2}")).ToArray());
        return new SessionManager(
            new StaticDefinitions(SampleDefinitions.Examiner),
            store,
            ids,
            new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero)),
            new VoiceAvailability { SpeechAdaptersResolved = true });
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

    private sealed class BarrierProfileStore(IMemoryStore inner) : IMemoryStore
    {
        private readonly TaskCompletionSource _bothLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _loads;

        public ValueTask<SessionSnapshot?> LoadAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            inner.LoadAsync(sessionId, cancellationToken);

        public ValueTask SaveAsync(
            SessionSnapshot snapshot,
            long expectedRevision,
            CancellationToken cancellationToken = default) =>
            inner.SaveAsync(snapshot, expectedRevision, cancellationToken);

        public ValueTask<IReadOnlyList<ConversationEntry>> ReadHistoryAsync(
            Guid sessionId,
            long afterEntrySequence,
            int limit,
            CancellationToken cancellationToken = default) =>
            inner.ReadHistoryAsync(sessionId, afterEntrySequence, limit, cancellationToken);

        public async ValueTask<UserProfile?> LoadProfileAsync(
            Guid profileId,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _loads) == 1)
            {
                await _bothLoaded.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _bothLoaded.TrySetResult();
            }

            return await inner.LoadProfileAsync(profileId, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask SaveProfileAsync(
            UserProfile profile,
            long expectedRevision,
            CancellationToken cancellationToken = default) =>
            inner.SaveProfileAsync(profile, expectedRevision, cancellationToken);

        public ValueTask RecoverCrashedSessionsAsync(CancellationToken cancellationToken = default) =>
            inner.RecoverCrashedSessionsAsync(cancellationToken);
    }

    private sealed class BarrierEndStore(IMemoryStore inner) : IMemoryStore
    {
        private readonly TaskCompletionSource _bothReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _endSaves;

        public ValueTask<SessionSnapshot?> LoadAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            inner.LoadAsync(sessionId, cancellationToken);

        public async ValueTask SaveAsync(
            SessionSnapshot snapshot,
            long expectedRevision,
            CancellationToken cancellationToken = default)
        {
            if (snapshot.Status == SessionStatus.Ended)
            {
                if (Interlocked.Increment(ref _endSaves) == 1)
                {
                    await _bothReady.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    _bothReady.TrySetResult();
                }
            }

            await inner.SaveAsync(snapshot, expectedRevision, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask<IReadOnlyList<ConversationEntry>> ReadHistoryAsync(
            Guid sessionId,
            long afterEntrySequence,
            int limit,
            CancellationToken cancellationToken = default) =>
            inner.ReadHistoryAsync(sessionId, afterEntrySequence, limit, cancellationToken);

        public ValueTask<UserProfile?> LoadProfileAsync(
            Guid profileId,
            CancellationToken cancellationToken = default) =>
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
