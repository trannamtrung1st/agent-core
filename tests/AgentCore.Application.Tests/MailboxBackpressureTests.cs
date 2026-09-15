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

public sealed class MailboxBackpressureTests
{
    [Fact]
    public async Task Saturated_mailbox_still_accepts_end()
    {
        var output = new CapturingSessionOutput();
        var inner = new InMemoryMemoryStore();
        var store = new GatedMemoryStore(inner);
        await using var runtime = Create(output, store);
        await runtime.AttachAsync();
        await output.WaitForAsync(item => item.Payload is ReadyOutput);
        store.Block = true;

        var blocked = runtime.SubmitUserTextAsync("hold the mailbox");
        await store.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var index = 0; index < 300; index++)
        {
            await runtime.SubmitUserTextAsync($"flood {index}");
        }

        var ending = runtime.RequestEndAsync();
        store.Gate.TrySetResult();
        Assert.True(await ending.WaitAsync(TimeSpan.FromSeconds(10)));
        await blocked;
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Status == SessionStatus.Ended)
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(SessionStatus.Ended, runtime.Snapshot.Status);
    }

    private static SessionRuntime Create(ISessionOutput output, IMemoryStore store)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 400).Select(index => Guid.Parse($"019944af-0000-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b849")]);
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
        store.SaveAsync(snapshot, 0).AsTask().GetAwaiter().GetResult();
        return new SessionRuntime(
            snapshot,
            new ScriptedLanguageModel(),
            new DefaultAgentBrain(new PromptContextBuilder()),
            store,
            output,
            ids,
            time,
            NullLogger<SessionRuntime>.Instance);
    }

    private sealed class GatedMemoryStore(IMemoryStore inner) : IMemoryStore
    {

        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<SessionSnapshot?> LoadAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            inner.LoadAsync(sessionId, cancellationToken);

        public bool Block { get; set; }

        public async ValueTask SaveAsync(
            SessionSnapshot snapshot,
            long expectedRevision,
            CancellationToken cancellationToken = default)
        {
            if (Block)
            {
                Blocked.TrySetResult();
                await Gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            await inner.SaveAsync(snapshot, expectedRevision, cancellationToken).ConfigureAwait(false);
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
