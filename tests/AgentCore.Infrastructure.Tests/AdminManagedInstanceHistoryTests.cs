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
    protected override Task ForEachProfileAsync(Func<ManagedInstanceHistoryFixture, Task> exercise)
    {
        var clock = TimeProvider.System;
        var ids = new SystemIdGenerator(clock);
        var events = new InMemoryAdminEventStore(ids);
        var instances = new InMemoryAgentInstanceStore { EventStore = events };
        var definitions = new SingleDefinitionStore(SampleExaminerDefinition());
        var service = new AdminAgentInstanceService(definitions, instances, events, ids, clock);
        return exercise(new ManagedInstanceHistoryFixture(instances, events, service, ids, clock));
    }

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

public sealed class SqliteAdminManagedInstanceHistoryTests : AdminManagedInstanceHistoryTests
{
    protected override async Task ForEachProfileAsync(Func<ManagedInstanceHistoryFixture, Task> exercise)
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-managed-history-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        var factory = new SqliteContextFactory(options);
        try
        {
            await new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync();
            var clock = TimeProvider.System;
            var ids = new SystemIdGenerator(clock);
            var events = new SqliteAdminEventStore(factory, ids);
            var instances = new SqliteAgentInstanceStore(factory, ids);
            var definitions = new SingleDefinitionStore(SampleExaminerDefinition());
            var service = new AdminAgentInstanceService(definitions, instances, events, ids, clock);
            await exercise(new ManagedInstanceHistoryFixture(instances, events, service, ids, clock));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private sealed class SqliteContextFactory(DbContextOptions<AgentCoreDbContext> options)
        : IDbContextFactory<AgentCoreDbContext>
    {
        public AgentCoreDbContext CreateDbContext() => new(options);

        public ValueTask<AgentCoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CreateDbContext());
    }
}

public abstract class AdminManagedInstanceHistoryTests
{
    protected sealed record ManagedInstanceHistoryFixture(
        IAgentInstanceStore Instances,
        IAdminEventStore Events,
        AdminAgentInstanceService Service,
        IIdGenerator Ids,
        TimeProvider Clock);

    protected abstract Task ForEachProfileAsync(Func<ManagedInstanceHistoryFixture, Task> exercise);

    [Fact]
    public async Task Create_managed_instance_records_managed_instance_created_event()
    {
        await ForEachProfileAsync(async fixture =>
        {
            var instance = await fixture.Service.CreateManagedAsync("examiner", 1, CancellationToken.None);
            var history = await fixture.Events.ListAsync(
                new AdminEventListQuery("agent.instance", instance.InstanceId.ToString("D")),
                CancellationToken.None);
            Assert.Single(history);
            Assert.Equal(AdminEventOperationKind.ManagedInstanceCreated, history[0].Operation);
            Assert.Equal(1, history[0].Version);
            using var summary = JsonDocument.Parse(history[0].SummaryJson);
            Assert.Equal("examiner", summary.RootElement.GetProperty("definitionId").GetString());
            Assert.Equal(instance.InstanceId.ToString("D"), summary.RootElement.GetProperty("instanceId").GetString());
            Assert.Equal(1, summary.RootElement.GetProperty("version").GetInt32());
        });
    }

    [Fact]
    public async Task Retry_with_same_operation_id_returns_same_instance_without_duplicate_history()
    {
        await ForEachProfileAsync(async fixture =>
        {
            var now = DateTimeOffset.Parse("2026-09-25T12:00:00Z");
            var operationId = Guid.Parse("019944af-00d1-7000-8000-000000000503");
            var instanceId = Guid.Parse("019944af-00d1-7000-8000-000000000504");
            var instance = SampleManagedInstance(now, instanceId);
            var append = AdminEventFactory.ManagedInstanceCreated(operationId, now, "examiner", instanceId, 1);
            var first = await fixture.Instances.InsertManagedWithHistoryAsync(instance, append, CancellationToken.None);
            var second = await fixture.Instances.InsertManagedWithHistoryAsync(instance, append, CancellationToken.None);
            Assert.Equal(first.InstanceId, second.InstanceId);
            var history = await fixture.Events.ListAsync(new AdminEventListQuery(), CancellationToken.None);
            Assert.Single(history, item => item.Operation == AdminEventOperationKind.ManagedInstanceCreated);
            Assert.Equal(operationId, history[0].OperationId);
        });
    }

    [Fact]
    public async Task Retry_with_different_operation_kind_is_rejected_even_when_target_guid_exists()
    {
        await ForEachProfileAsync(async fixture =>
        {
            var now = DateTimeOffset.Parse("2026-09-25T12:00:00Z");
            var operationId = Guid.Parse("019944af-00d1-7000-8000-000000000509");
            var sharedTargetId = Guid.Parse("019944af-00d1-7000-8000-000000000510");
            var draftAppend = AdminEventFactory.DraftCreated(
                operationId,
                now,
                "examiner",
                sharedTargetId,
                DefinitionDraftSourceKind.New,
                null);
            if (fixture.Events is InMemoryAdminEventStore inMemoryEvents)
            {
                inMemoryEvents.AppendWithinLock(draftAppend);
            }
            else
            {
                await fixture.Events.AppendAsync(draftAppend, CancellationToken.None);
            }

            var managedAppend = AdminEventFactory.ManagedInstanceCreated(
                operationId,
                now,
                "examiner",
                sharedTargetId,
                1);
            var error = await Assert.ThrowsAsync<AgentCoreException>(() =>
                fixture.Instances.InsertManagedWithHistoryAsync(
                    SampleManagedInstance(now, sharedTargetId),
                    managedAppend,
                    CancellationToken.None).AsTask());
            Assert.Equal("Conflict", error.Code);
            Assert.Contains("different admin event", error.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Retry_with_same_operation_id_but_different_instance_target_is_rejected()
    {
        await ForEachProfileAsync(async fixture =>
        {
            var now = DateTimeOffset.Parse("2026-09-25T12:00:00Z");
            var operationId = Guid.Parse("019944af-00d1-7000-8000-000000000511");
            var firstInstanceId = Guid.Parse("019944af-00d1-7000-8000-000000000512");
            var secondInstanceId = Guid.Parse("019944af-00d1-7000-8000-000000000513");
            var firstAppend = AdminEventFactory.ManagedInstanceCreated(
                operationId,
                now,
                "examiner",
                firstInstanceId,
                1);
            _ = await fixture.Instances.InsertManagedWithHistoryAsync(
                SampleManagedInstance(now, firstInstanceId),
                firstAppend,
                CancellationToken.None);
            var conflictingAppend = AdminEventFactory.ManagedInstanceCreated(
                operationId,
                now,
                "examiner",
                secondInstanceId,
                1);
            var error = await Assert.ThrowsAsync<AgentCoreException>(() =>
                fixture.Instances.InsertManagedWithHistoryAsync(
                    SampleManagedInstance(now, secondInstanceId),
                    conflictingAppend,
                    CancellationToken.None).AsTask());
            Assert.Equal("Conflict", error.Code);
            Assert.Contains("does not match the retried command", error.Message, StringComparison.Ordinal);
            Assert.Null(await fixture.Instances.FindAsync(secondInstanceId, CancellationToken.None));
        });
    }

    [Fact]
    public async Task Concurrent_retry_with_same_operation_id_persists_one_instance()
    {
        await ForEachProfileAsync(async fixture =>
        {
            var now = DateTimeOffset.Parse("2026-09-25T12:00:00Z");
            var operationId = Guid.Parse("019944af-00d1-7000-8000-000000000505");
            var instanceId = Guid.Parse("019944af-00d1-7000-8000-000000000506");
            var instance = SampleManagedInstance(now, instanceId);
            var append = AdminEventFactory.ManagedInstanceCreated(operationId, now, "examiner", instanceId, 1);
            var startBarrier = new Barrier(2);
            var first = Task.Run(() =>
            {
                startBarrier.SignalAndWait();
                return fixture.Instances.InsertManagedWithHistoryAsync(instance, append, CancellationToken.None).AsTask();
            });
            var second = Task.Run(() =>
            {
                startBarrier.SignalAndWait();
                return fixture.Instances.InsertManagedWithHistoryAsync(instance, append, CancellationToken.None).AsTask();
            });
            var results = await Task.WhenAll(first, second);
            Assert.Equal(results[0].InstanceId, results[1].InstanceId);
            var history = await fixture.Events.ListAsync(new AdminEventListQuery(), CancellationToken.None);
            Assert.Single(
                history,
                item => item.Operation == AdminEventOperationKind.ManagedInstanceCreated && item.OperationId == operationId);
        });
    }

    internal static AgentInstance SampleManagedInstance(DateTimeOffset now, Guid instanceId) =>
        new(
            instanceId,
            "examiner",
            1,
            new AgentIdentity("Alex", "Examiner", "Practice.", "Calm"),
            AgentInstanceLifecycle.Active,
            now,
            now,
            Compatibility: false);

    protected static AgentDefinition SampleExaminerDefinition() =>
        new(
            1,
            "examiner",
            1,
            new AgentIdentity("Alex", "Examiner", "Practice.", "Calm"),
            ["Conduct practice"],
            "You are Alex.",
            new BehaviorPolicy("acknowledgeThenContinue", true, true),
            new ConversationPolicy("concise", true, "en", 256),
            new InitiativePolicy(true, 8000, 30000, 1, ["longSilence"]),
            new VoiceConfiguration(true, "default", 1.0),
            new ProviderPreferences("primary-llm", "primary-stt", "primary-tts"),
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
