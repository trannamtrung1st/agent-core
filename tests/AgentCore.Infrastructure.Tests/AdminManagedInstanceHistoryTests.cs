using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Admin;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;

public sealed class InMemoryAdminManagedInstanceHistoryTests : AdminManagedInstanceHistoryTests
{

    [Fact]
    public async Task Insert_rolls_back_when_history_append_fails()
    {
        var clock = TimeProvider.System;
        var ids = new SystemIdGenerator(clock);
        var instances = new InMemoryAgentInstanceStore { EventStore = new ThrowingAdminEventStore(ids) };
        var now = DateTimeOffset.Parse("2026-09-25T12:00:00Z");
        var instance = SampleManagedInstance(now, Guid.Parse("019944af-00d1-7000-8000-000000000501"));
        var append = AdminEventFactory.ManagedInstanceCreated(
            Guid.Parse("019944af-00d1-7000-8000-000000000502"),
            now,
            "examiner",
            instance.InstanceId,
            1);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            instances.InsertManagedWithHistoryAsync(instance, append, CancellationToken.None).AsTask());
        Assert.Null(await instances.FindAsync(instance.InstanceId, CancellationToken.None));
    }

    [Fact]
    public async Task Concurrent_retry_waits_on_operation_lock_during_managed_instance_append()
    {
        var ids = new SystemIdGenerator(TimeProvider.System);
        var firstAppendHolding = new ManualResetEventSlim(false);
        var releaseFirstAppend = new ManualResetEventSlim(false);
        var events = new HaltedManagedInstanceAdminEventStore(ids)
        {
            FirstManagedInstanceAppendHolding = firstAppendHolding,
            ReleaseFirstManagedInstanceAppend = releaseFirstAppend
        };
        var instances = new InMemoryAgentInstanceStore { EventStore = events };
        var now = DateTimeOffset.Parse("2026-09-25T12:00:00Z");
        var operationId = Guid.Parse("019944af-00d1-7000-8000-000000000507");
        var instanceId = Guid.Parse("019944af-00d1-7000-8000-000000000508");
        var instance = SampleManagedInstance(now, instanceId);
        var append = AdminEventFactory.ManagedInstanceCreated(operationId, now, "examiner", instanceId, 1);
        var first = Task.Run(() => instances.InsertManagedWithHistoryAsync(instance, append, CancellationToken.None).AsTask());
        Assert.True(firstAppendHolding.Wait(TimeSpan.FromSeconds(5)));
        var retryEntered = new ManualResetEventSlim(false);
        var second = Task.Run(() =>
        {
            retryEntered.Set();
            return instances.InsertManagedWithHistoryAsync(instance, append, CancellationToken.None).AsTask();
        });
        Assert.True(retryEntered.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(second.IsCompleted);
        releaseFirstAppend.Set();
        var results = await Task.WhenAll(first, second);
        Assert.Equal(results[0].InstanceId, results[1].InstanceId);
        var history = await events.ListAsync(new AdminEventListQuery(), CancellationToken.None);
        Assert.Single(history, item => item.Operation == AdminEventOperationKind.ManagedInstanceCreated);
    }
}

public abstract class AdminManagedInstanceHistoryTests
{

    internal static AgentInstance SampleManagedInstance(DateTimeOffset now, Guid instanceId) =>
        new(
            instanceId,
            "examiner",
            1,
            new AgentIdentity("Alex", "Examiner", "Practice.", "Calm"),
            AgentInstanceLifecycle.Active,
            now,
            now);

    protected static AgentDefinition SampleExaminerDefinition() =>
        new(
            2,
            "examiner",
            1,
            new AgentIdentity("Alex", "Examiner", "Practice.", "Calm"),
            ["Conduct practice"],
            "You are Alex.",
            new BehaviorPolicy("acknowledgeThenContinue", true, true),
            new ConversationPolicy("concise", true, "en", 256),
            new InitiativePolicy(true, 8000, 30000, 1, ["longSilence"]),
            new VoiceConfiguration(true, "default", 1.0),
            new ProviderPreferences("primary-stt", "primary-tts"),
            new Dictionary<string, string>());

    internal sealed class SingleDefinitionStore(AgentDefinition definition) : IAgentDefinitionStore
    {
        public ValueTask<AgentDefinition?> GetAsync(
            string id,
            int? version = null,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                definition.Id == id && (version is null || definition.Version == version) ? definition : null);

        public ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AgentDefinition>>([definition]);
    }
}

internal sealed class HaltedManagedInstanceAdminEventStore(IIdGenerator ids) : InMemoryAdminEventStore(ids)
{
    internal ManualResetEventSlim? FirstManagedInstanceAppendHolding { get; set; }

    internal ManualResetEventSlim? ReleaseFirstManagedInstanceAppend { get; set; }

    private int _managedInstanceAppendAttempts;

    internal override void AppendWithinLock(AdminEventAppend append)
    {
        if (append.Operation != AdminEventOperationKind.ManagedInstanceCreated)
        {
            base.AppendWithinLock(append);
            return;
        }

        if (Interlocked.Increment(ref _managedInstanceAppendAttempts) == 1)
        {
            FirstManagedInstanceAppendHolding?.Set();
            if (ReleaseFirstManagedInstanceAppend is null
                || !ReleaseFirstManagedInstanceAppend.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new InvalidOperationException("Managed instance history append synchronization timed out.");
            }
        }

        base.AppendWithinLock(append);
    }
}
