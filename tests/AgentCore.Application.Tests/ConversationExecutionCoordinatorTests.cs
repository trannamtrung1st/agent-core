using AgentCore.Application.Conversation;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Work;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class ConversationExecutionCoordinatorTests
{
    [Fact]
    public async Task Missing_session_fails_the_execution_and_the_next_item_still_dispatches()
    {
        var store = new InMemoryConversationTurnExecutionStore();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero));
        var missing = Guid.Parse("019944af-0000-7000-8000-0000000000a1");
        var present = Guid.Parse("019944af-0000-7000-8000-0000000000a2");
        await store.CreateAsync(Item(missing, time.GetUtcNow()));
        await store.CreateAsync(Item(present, time.GetUtcNow().AddSeconds(1)));
        var runner = new ScriptedRunner(session =>
            session == missing
                ? throw AgentCoreErrors.NotFound("Session was not found.")
                : true);
        var coordinator = new ConversationExecutionCoordinator(
            store,
            runner,
            new SystemIdGenerator(time),
            time);

        var ran = await coordinator.ExecuteRunnableAsync(8);

        Assert.Equal(1, ran);
        Assert.Equal([missing, present], runner.Sessions);
        Assert.Equal(ConversationTurnExecutionStatus.Failed, (await store.GetAsync(missing))!.Status);
        Assert.Equal(ConversationTurnExecutionStatus.Running, (await store.GetAsync(present))!.Status);
        time.Advance(TimeSpan.FromMinutes(10));
        await store.RecoverExpiredClaimsAsync(time.GetUtcNow());
        var runnable = await store.ListRunnableAsync(time.GetUtcNow(), 8);
        Assert.DoesNotContain(runnable, item => item.ExecutionId == missing);
        Assert.Contains(runnable, item => item.ExecutionId == present);
    }

    [Fact]
    public async Task Transient_dispatch_failure_stays_claimed_instead_of_terminal()
    {
        var store = new InMemoryConversationTurnExecutionStore();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero));
        var session = Guid.Parse("019944af-0000-7000-8000-0000000000b1");
        await store.CreateAsync(Item(session, time.GetUtcNow()));
        var runner = new ScriptedRunner(_ => throw new InvalidOperationException("temporary"));
        var coordinator = new ConversationExecutionCoordinator(
            store,
            runner,
            new SystemIdGenerator(time),
            time);

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ExecuteRunnableAsync(8).AsTask());
        var stored = (await store.GetAsync(session))!;
        Assert.Equal(ConversationTurnExecutionStatus.Running, stored.Status);
        Assert.NotNull(stored.Claim);
    }

    private static ConversationTurnExecution Item(Guid sessionId, DateTimeOffset accepted) =>
        ConversationTurnExecution.AcceptNew(
            sessionId,
            sessionId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            null,
            "general-assistant",
            12,
            null,
            new WorkModelPin("synthetic", "synthetic", "scripted", null),
            accepted);

    private sealed class ScriptedRunner(Func<Guid, bool> dispatch) : IConversationTurnRunner
    {
        public List<Guid> Sessions { get; } = [];

        public ValueTask<bool> DispatchAsync(ConversationTurnExecution execution, CancellationToken cancellationToken = default)
        {
            Sessions.Add(execution.SessionId);
            return new(dispatch(execution.SessionId));
        }
    }
}
