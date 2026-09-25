using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Admin;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;

public sealed class InMemoryAdminInstanceLifecycleHistoryTests : AdminInstanceLifecycleHistoryTests
{
    protected override Task ForEachProfileAsync(Func<LifecycleHistoryFixture, Task> exercise)
    {
        var clock = TimeProvider.System;
        var ids = new SystemIdGenerator(clock);
        var events = new InMemoryAdminEventStore(ids);
        var instances = new InMemoryAgentInstanceStore { EventStore = events };
        var service = new AdminAgentInstanceService(
            new AdminInstanceDefinitionVersionHistoryTests.VersionedDefinitionStore(SampleDefinition()),
            instances,
            events,
            ids,
            clock);
        return exercise(new LifecycleHistoryFixture(instances, events, service, clock));
    }
}

public sealed class SqliteAdminInstanceLifecycleHistoryTests : AdminInstanceLifecycleHistoryTests
{
    protected override async Task ForEachProfileAsync(Func<LifecycleHistoryFixture, Task> exercise)
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-lifecycle-history-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        var factory = new SqliteContextFactory(options);
        try
        {
            await new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync();
            var clock = TimeProvider.System;
            var ids = new SystemIdGenerator(clock);
            var events = new SqliteAdminEventStore(factory, ids);
            var instances = new SqliteAgentInstanceStore(factory, ids);
            var service = new AdminAgentInstanceService(
                new AdminInstanceDefinitionVersionHistoryTests.VersionedDefinitionStore(SampleDefinition()),
                instances,
                events,
                ids,
                clock);
            await exercise(new LifecycleHistoryFixture(instances, events, service, clock));
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

public abstract class AdminInstanceLifecycleHistoryTests
{
    protected sealed record LifecycleHistoryFixture(
        IAgentInstanceStore Instances,
        IAdminEventStore Events,
        AdminAgentInstanceService Service,
        TimeProvider Clock);

    protected abstract Task ForEachProfileAsync(Func<LifecycleHistoryFixture, Task> exercise);

    [Fact]
    public async Task Archive_records_instance_archived_event()
    {
        await ForEachProfileAsync(async fixture =>
        {
            var now = DateTimeOffset.Parse("2026-09-25T12:00:00Z");
            var instanceId = Guid.Parse("019944af-00d1-7000-8000-000000000801");
            await fixture.Instances.InsertAsync(
                AdminManagedInstanceHistoryTests.SampleManagedInstance(now, instanceId),
                CancellationToken.None);
            _ = await fixture.Service.SetLifecycleAsync(instanceId, AgentInstanceLifecycle.Archived, 1, CancellationToken.None);
            var history = await fixture.Events.ListAsync(
                new AdminEventListQuery("agent.instance", instanceId.ToString("D")),
                CancellationToken.None);
            Assert.Contains(history, item => item.Operation == AdminEventOperationKind.InstanceArchived);
            var archived = history.Single(item => item.Operation == AdminEventOperationKind.InstanceArchived);
            using var summary = JsonDocument.Parse(archived.SummaryJson);
            Assert.Equal("Active", summary.RootElement.GetProperty("fromLifecycle").GetString());
            Assert.Equal("Archived", summary.RootElement.GetProperty("toLifecycle").GetString());
        });
    }

    [Fact]
    public async Task Unarchive_records_instance_unarchived_event()
    {
        await ForEachProfileAsync(async fixture =>
        {
            var now = DateTimeOffset.Parse("2026-09-25T12:00:00Z");
            var instanceId = Guid.Parse("019944af-00d1-7000-8000-000000000802");
            await fixture.Instances.InsertAsync(
                AdminManagedInstanceHistoryTests.SampleManagedInstance(now, instanceId),
                CancellationToken.None);
            _ = await fixture.Service.SetLifecycleAsync(instanceId, AgentInstanceLifecycle.Archived, 1, CancellationToken.None);
            var archived = await fixture.Instances.FindAsync(instanceId, CancellationToken.None);
            _ = await fixture.Service.SetLifecycleAsync(
                instanceId,
                AgentInstanceLifecycle.Active,
                archived!.Revision,
                CancellationToken.None);
            var history = await fixture.Events.ListAsync(
                new AdminEventListQuery("agent.instance", instanceId.ToString("D")),
                CancellationToken.None);
            Assert.Contains(history, item => item.Operation == AdminEventOperationKind.InstanceUnarchived);
        });
    }

    [Fact]
    public async Task Retry_with_same_operation_id_but_different_target_lifecycle_is_rejected()
    {
        await ForEachProfileAsync(async fixture =>
        {
            var now = DateTimeOffset.Parse("2026-09-25T12:00:00Z");
            var instanceId = Guid.Parse("019944af-00d1-7000-8000-000000000803");
            await fixture.Instances.InsertAsync(
                AdminManagedInstanceHistoryTests.SampleManagedInstance(now, instanceId),
                CancellationToken.None);
            var operationId = Guid.Parse("019944af-00d1-7000-8000-000000000804");
            var archiveAppend = AdminEventFactory.InstanceLifecycleChanged(
                operationId,
                now,
                "examiner",
                instanceId,
                1,
                AgentInstanceLifecycle.Active,
                AgentInstanceLifecycle.Archived,
                2);
            _ = await fixture.Instances.UpdateLifecycleWithHistoryAsync(
                new AgentInstanceRevisionUpdate(instanceId, 1, Lifecycle: AgentInstanceLifecycle.Archived),
                now,
                archiveAppend,
                CancellationToken.None);
            var conflictingAppend = AdminEventFactory.InstanceLifecycleChanged(
                operationId,
                now,
                "examiner",
                instanceId,
                1,
                AgentInstanceLifecycle.Active,
                AgentInstanceLifecycle.Archived,
                2);
            var error = await Assert.ThrowsAsync<AgentCoreException>(() =>
                fixture.Instances.UpdateLifecycleWithHistoryAsync(
                    new AgentInstanceRevisionUpdate(instanceId, 1, Lifecycle: AgentInstanceLifecycle.Active),
                    now,
                    conflictingAppend,
                    CancellationToken.None).AsTask());
            Assert.Equal("Conflict", error.Code);
            Assert.Contains("does not match the retried command", error.Message, StringComparison.Ordinal);
        });
    }

    protected static AgentDefinition SampleDefinition() =>
        AdminInstanceDefinitionVersionHistoryTests.SampleExaminerDefinition(1);
}
