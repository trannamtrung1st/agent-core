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

public sealed class InMemoryAdminEventStoreContractTests : AdminEventStoreContractTests
{
    protected override Task ForEachStoreAsync(Func<IAdminEventStore, Task> exercise) =>
        exercise(new InMemoryAdminEventStore(new SystemIdGenerator(TimeProvider.System)));
}

public sealed class SqliteAdminEventStoreContractTests : AdminEventStoreContractTests
{
    protected override async Task ForEachStoreAsync(Func<IAdminEventStore, Task> exercise)
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-admin-events-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        var factory = new SqliteContextFactory(options);
        try
        {
            await new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync();
            await exercise(new SqliteAdminEventStore(factory, new SystemIdGenerator(TimeProvider.System)));
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

public abstract class AdminEventStoreContractTests
{
    protected abstract Task ForEachStoreAsync(Func<IAdminEventStore, Task> exercise);

    [Theory]
    [InlineData(AdminLearnedMemoryScope.Session)]
    [InlineData(AdminLearnedMemoryScope.IdentityUser)]
    [InlineData(AdminLearnedMemoryScope.User)]
    public async Task Historical_Memory_and_Automation_events_remain_readable_without_retired_mutators(AdminLearnedMemoryScope scope)
    {
        await ForEachStoreAsync(async store =>
        {
            var at = DateTimeOffset.Parse("2026-09-25T12:00:00Z"); var instance = Guid.NewGuid();
            Guid? session = scope == AdminLearnedMemoryScope.Session ? Guid.NewGuid() : null;
            var records = new[] {
                AdminEventFactory.MemoryItemDeleted(Guid.NewGuid(), at, instance, scope, Guid.NewGuid(), session),
                AdminEventFactory.MemoryScopeReset(Guid.NewGuid(), at, instance, scope, 2, session),
                AdminEventFactory.AutomationRevoked(Guid.NewGuid(), at, instance, Guid.NewGuid(), 3)
            };
            foreach (var append in records)
            {
                var saved = await store.AppendAsync(append, CancellationToken.None);
                var retained = await store.TryGetByOperationIdAsync(append.OperationId, CancellationToken.None);
                Assert.NotNull(retained); Assert.Equal(saved.EventId, retained.EventId);
                Assert.Equal(append.Operation, retained.Operation); Assert.Equal(append.SummaryJson, retained.SummaryJson);
                using var summary = System.Text.Json.JsonDocument.Parse(retained.SummaryJson);
                Assert.False(summary.RootElement.TryGetProperty("content", out _));
                Assert.False(summary.RootElement.TryGetProperty("subject", out _));
                if (append.Operation != AdminEventOperationKind.AutomationRevoked)
                    Assert.Equal(scope.ToString(), summary.RootElement.GetProperty("scope").GetString());
                if (append.Operation == AdminEventOperationKind.MemoryScopeReset)
                    Assert.Equal(2, summary.RootElement.GetProperty("itemsRemoved").GetInt32());
            }
        });
    }

    [Fact]
    public async Task Append_is_idempotent_by_operation_id()
    {
        await ForEachStoreAsync(async store =>
        {
            var operationId = Guid.Parse("019944af-00d1-7000-8000-000000000101");
            var occurredAt = DateTimeOffset.Parse("2026-09-25T12:00:00Z");
            var append = AdminEventFactory.PublicationCreated(
                operationId,
                occurredAt,
                "examiner",
                2,
                Guid.Parse("019944af-00d1-7000-8000-000000000099"),
                3,
                ["instructions", "triggerPolicy"]);
            var first = await store.AppendAsync(append, CancellationToken.None);
            var second = await store.AppendAsync(append, CancellationToken.None);
            Assert.Equal(first.EventId, second.EventId);
            Assert.Equal(AdminEventOperationKind.PublicationCreated, second.Operation);
        });
    }

    [Fact]
    public async Task List_filters_by_target()
    {
        await ForEachStoreAsync(async store =>
        {
            var occurredAt = DateTimeOffset.Parse("2026-09-25T12:00:00Z");
            await store.AppendAsync(
                AdminEventFactory.PublicationCreated(
                    Guid.NewGuid(),
                    occurredAt,
                    "examiner",
                    2,
                    Guid.NewGuid(),
                    1,
                    ["instructions"]),
                CancellationToken.None);
            await store.AppendAsync(
                AdminEventFactory.PublicationCreated(
                    Guid.NewGuid(),
                    occurredAt.AddMinutes(1),
                    "coach",
                    1,
                    Guid.NewGuid(),
                    1,
                    ["capabilities"]),
                CancellationToken.None);

            var items = await store.ListAsync(
                new AdminEventListQuery("definition.publication", "examiner:2"),
                CancellationToken.None);
            Assert.Single(items);
            Assert.Equal("examiner:2", items[0].TargetId);
        });
    }

    [Fact]
    public async Task Both_stores_reject_malformed_summary_json()
    {
        await ForEachStoreAsync(async store =>
        {
            var append = new AdminEventAppend(
                Guid.NewGuid(),
                DateTimeOffset.Parse("2026-09-25T12:00:00Z"),
                AdminEventActorKind.LocalOwner,
                AdminEventOperationKind.PublicationCreated,
                "definition.publication",
                "examiner:1",
                1,
                1,
                "not-json");
            var error = await Assert.ThrowsAsync<AgentCoreException>(() =>
                store.AppendAsync(append, CancellationToken.None).AsTask());
            Assert.Equal(400, error.StatusCode);
        });
    }

    [Fact]
    public async Task Both_stores_accept_instance_lifecycle_summary()
    {
        await ForEachStoreAsync(async store =>
        {
            var instanceId = Guid.Parse("019944af-00d1-7000-8000-000000000095");
            var append = AdminEventFactory.InstanceLifecycleChanged(
                Guid.NewGuid(),
                DateTimeOffset.Parse("2026-09-25T12:00:00Z"),
                "examiner",
                instanceId,
                1,
                AgentInstanceLifecycle.Active,
                AgentInstanceLifecycle.Archived,
                2);
            await store.AppendAsync(append, CancellationToken.None);
        });
    }

    [Fact]
    public void Publication_factory_rejects_unknown_changed_section()
    {
        var error = Assert.Throws<AgentCoreException>(() =>
            AdminEventFactory.PublicationCreated(
                Guid.NewGuid(),
                DateTimeOffset.Parse("2026-09-25T12:00:00Z"),
                "examiner",
                1,
                Guid.NewGuid(),
                1,
                ["secretPayload"]));
        Assert.Equal(400, error.StatusCode);
    }
}
