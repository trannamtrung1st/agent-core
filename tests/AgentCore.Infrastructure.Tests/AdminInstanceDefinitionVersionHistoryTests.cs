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

public sealed class InMemoryAdminInstanceDefinitionVersionHistoryTests : AdminInstanceDefinitionVersionHistoryTests
{
    protected override Task ForEachProfileAsync(Func<InstanceVersionHistoryFixture, Task> exercise)
    {
        var clock = TimeProvider.System;
        var ids = new SystemIdGenerator(clock);
        var events = new InMemoryAdminEventStore(ids);
        var instances = new InMemoryAgentInstanceStore { EventStore = events };
        var definitions = new VersionedDefinitionStore(SampleExaminerDefinition(1), SampleExaminerDefinition(2));
        var service = new AdminAgentInstanceService(definitions, instances, events, ids, clock);
        return exercise(new InstanceVersionHistoryFixture(instances, events, service, ids, clock));
    }

    [Fact]
    public async Task Reassociate_rolls_back_when_history_append_fails()
    {
        var clock = TimeProvider.System;
        var ids = new SystemIdGenerator(clock);
        var instances = new InMemoryAgentInstanceStore { EventStore = new ThrowingAdminEventStore(ids) };
        var now = DateTimeOffset.Parse("2026-09-25T12:00:00Z");
        var instanceId = Guid.Parse("019944af-00d1-7000-8000-000000000601");
        var instance = AdminManagedInstanceHistoryTests.SampleManagedInstance(now, instanceId);
        await instances.InsertAsync(instance, CancellationToken.None);
        var append = AdminEventFactory.InstanceDefinitionVersionChanged(
            Guid.Parse("019944af-00d1-7000-8000-000000000602"),
            now,
            "examiner",
            instanceId,
            1,
            2);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            instances.UpdateActiveVersionWithHistoryAsync(
                new AgentInstanceRevisionUpdate(instanceId, 1, ActiveVersion: 2),
                now,
                append,
                CancellationToken.None).AsTask());
        var reloaded = await instances.FindAsync(instanceId, CancellationToken.None);
        Assert.NotNull(reloaded);
        Assert.Equal(1, reloaded!.ActiveVersion);
        Assert.Equal(1, reloaded.Revision);
    }
}

public sealed class SqliteAdminInstanceDefinitionVersionHistoryTests : AdminInstanceDefinitionVersionHistoryTests
{
    protected override async Task ForEachProfileAsync(Func<InstanceVersionHistoryFixture, Task> exercise)
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-version-history-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        var factory = new SqliteContextFactory(options);
        try
        {
            await new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync();
            var clock = TimeProvider.System;
            var ids = new SystemIdGenerator(clock);
            var events = new SqliteAdminEventStore(factory, ids);
            var instances = new SqliteAgentInstanceStore(factory, ids);
            var definitions = new VersionedDefinitionStore(SampleExaminerDefinition(1), SampleExaminerDefinition(2));
            var service = new AdminAgentInstanceService(definitions, instances, events, ids, clock);
            await exercise(new InstanceVersionHistoryFixture(instances, events, service, ids, clock));
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

public abstract class AdminInstanceDefinitionVersionHistoryTests
{
    protected sealed record InstanceVersionHistoryFixture(
        IAgentInstanceStore Instances,
        IAdminEventStore Events,
        AdminAgentInstanceService Service,
        IIdGenerator Ids,
        TimeProvider Clock);

    protected abstract Task ForEachProfileAsync(Func<InstanceVersionHistoryFixture, Task> exercise);

    [Fact]
    public async Task Reassociate_active_version_records_instance_definition_version_changed_event()
    {
        await ForEachProfileAsync(async fixture =>
        {
            var now = DateTimeOffset.Parse("2026-09-25T12:00:00Z");
            var instanceId = Guid.Parse("019944af-00d1-7000-8000-000000000603");
            await fixture.Instances.InsertAsync(
                AdminManagedInstanceHistoryTests.SampleManagedInstance(now, instanceId),
                CancellationToken.None);
            var updated = await fixture.Service.ReassociateActiveVersionAsync(instanceId, 2, 1, CancellationToken.None);
            Assert.Equal(2, updated.ActiveVersion);
            Assert.Equal(2, updated.Revision);
            var history = await fixture.Events.ListAsync(
                new AdminEventListQuery("agent.instance", instanceId.ToString("D")),
                CancellationToken.None);
            Assert.Contains(history, item => item.Operation == AdminEventOperationKind.InstanceDefinitionVersionChanged);
            var versionEvent = history.Single(item => item.Operation == AdminEventOperationKind.InstanceDefinitionVersionChanged);
            using var summary = JsonDocument.Parse(versionEvent.SummaryJson);
            Assert.Equal(1, summary.RootElement.GetProperty("fromVersion").GetInt32());
            Assert.Equal(2, summary.RootElement.GetProperty("toVersion").GetInt32());
        });
    }

    [Fact]
    public async Task Retry_with_same_operation_id_returns_same_revision_without_duplicate_history()
    {
        await ForEachProfileAsync(async fixture =>
        {
            var now = DateTimeOffset.Parse("2026-09-25T12:00:00Z");
            var instanceId = Guid.Parse("019944af-00d1-7000-8000-000000000604");
            await fixture.Instances.InsertAsync(
                AdminManagedInstanceHistoryTests.SampleManagedInstance(now, instanceId),
                CancellationToken.None);
            var operationId = Guid.Parse("019944af-00d1-7000-8000-000000000605");
            var append = AdminEventFactory.InstanceDefinitionVersionChanged(
                operationId,
                now,
                "examiner",
                instanceId,
                1,
                2);
            var first = await fixture.Instances.UpdateActiveVersionWithHistoryAsync(
                new AgentInstanceRevisionUpdate(instanceId, 1, ActiveVersion: 2),
                now,
                append,
                CancellationToken.None);
            var second = await fixture.Instances.UpdateActiveVersionWithHistoryAsync(
                new AgentInstanceRevisionUpdate(instanceId, 1, ActiveVersion: 2),
                now,
                append,
                CancellationToken.None);
            Assert.Equal(first.Revision, second.Revision);
            var history = await fixture.Events.ListAsync(new AdminEventListQuery(), CancellationToken.None);
            Assert.Single(history, item => item.Operation == AdminEventOperationKind.InstanceDefinitionVersionChanged);
        });
    }

    [Fact]
    public async Task Retry_with_same_operation_id_but_different_target_version_is_rejected()
    {
        await ForEachProfileAsync(async fixture =>
        {
            var now = DateTimeOffset.Parse("2026-09-25T12:00:00Z");
            var instanceId = Guid.Parse("019944af-00d1-7000-8000-000000000608");
            await fixture.Instances.InsertAsync(
                AdminManagedInstanceHistoryTests.SampleManagedInstance(now, instanceId),
                CancellationToken.None);
            var operationId = Guid.Parse("019944af-00d1-7000-8000-000000000609");
            var firstAppend = AdminEventFactory.InstanceDefinitionVersionChanged(
                operationId,
                now,
                "examiner",
                instanceId,
                1,
                2);
            _ = await fixture.Instances.UpdateActiveVersionWithHistoryAsync(
                new AgentInstanceRevisionUpdate(instanceId, 1, ActiveVersion: 2),
                now,
                firstAppend,
                CancellationToken.None);
            var conflictingAppend = AdminEventFactory.InstanceDefinitionVersionChanged(
                operationId,
                now,
                "examiner",
                instanceId,
                1,
                3);
            var error = await Assert.ThrowsAsync<AgentCoreException>(() =>
                fixture.Instances.UpdateActiveVersionWithHistoryAsync(
                    new AgentInstanceRevisionUpdate(instanceId, 1, ActiveVersion: 3),
                    now,
                    conflictingAppend,
                    CancellationToken.None).AsTask());
            Assert.Equal("Conflict", error.Code);
            Assert.Contains("does not match the retried command", error.Message, StringComparison.Ordinal);
            var reloaded = await fixture.Instances.FindAsync(instanceId, CancellationToken.None);
            Assert.NotNull(reloaded);
            Assert.Equal(2, reloaded!.ActiveVersion);
        });
    }

    [Fact]
    public async Task Retry_with_different_operation_kind_is_rejected()
    {
        await ForEachProfileAsync(async fixture =>
        {
            var now = DateTimeOffset.Parse("2026-09-25T12:00:00Z");
            var operationId = Guid.Parse("019944af-00d1-7000-8000-000000000606");
            var instanceId = Guid.Parse("019944af-00d1-7000-8000-000000000607");
            var draftAppend = AdminEventFactory.DraftCreated(
                operationId,
                now,
                "examiner",
                instanceId,
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

            var versionAppend = AdminEventFactory.InstanceDefinitionVersionChanged(
                operationId,
                now,
                "examiner",
                instanceId,
                1,
                2);
            await fixture.Instances.InsertAsync(
                AdminManagedInstanceHistoryTests.SampleManagedInstance(now, instanceId),
                CancellationToken.None);
            var error = await Assert.ThrowsAsync<AgentCoreException>(() =>
                fixture.Instances.UpdateActiveVersionWithHistoryAsync(
                    new AgentInstanceRevisionUpdate(instanceId, 1, ActiveVersion: 2),
                    now,
                    versionAppend,
                    CancellationToken.None).AsTask());
            Assert.Equal("Conflict", error.Code);
            Assert.Contains("different admin event", error.Message, StringComparison.Ordinal);
        });
    }

    internal static AgentDefinition SampleExaminerDefinition(int version) =>
        new(
            1,
            "examiner",
            version,
            new AgentIdentity("Alex", "Examiner", "Practice.", "Calm"),
            ["Conduct practice"],
            "You are Alex.",
            new BehaviorPolicy("acknowledgeThenContinue", true, true),
            new ConversationPolicy("concise", true, "en", 256),
            new InitiativePolicy(true, 8000, 30000, 1, ["longSilence"]),
            new VoiceConfiguration(true, "default", 1.0),
            new ProviderPreferences("primary-llm", "primary-stt", "primary-tts"),
            new Dictionary<string, string>());

    internal sealed class VersionedDefinitionStore(params AgentDefinition[] definitions) : IAgentDefinitionStore
    {
        public ValueTask<AgentDefinition?> GetAsync(
            string id,
            int? version = null,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                definitions.FirstOrDefault(item => item.Id == id && (version is null || item.Version == version)));

        public ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AgentDefinition>>(definitions);
    }
}
