using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;

public abstract class AgentInstanceStoreContractTests
{
    protected abstract Task ForEachStoresAsync(Func<IAgentInstanceStore, Task> exercise);

    [Fact]
    public async Task Budget_overrides_are_revision_checked_independent_and_can_return_to_inheritance()
    {
        await ForEachStoresAsync(async store =>
        {
            var now = DateTimeOffset.Parse("2026-10-09T00:00:00Z");
            var first = SampleManaged(now); var second = first with { InstanceId = Guid.NewGuid() };
            await store.InsertAsync(first); await store.InsertAsync(second);
            var policy = new ExecutionBudgetPolicy(InteractiveBrowser: ExecutionBudgetProfile.For(ExecutionBudgetClass.InteractiveBrowser, ExecutionBudgetPreset.DeepWorkflow));
            var updated = await store.UpdateWithExpectedRevisionAsync(new(first.InstanceId, 1, SetExecutionBudgets: true, ExecutionBudgets: policy), now.AddSeconds(1));
            Assert.Equal(policy, updated.ExecutionBudgets); Assert.Equal(policy, (await store.FindAsync(first.InstanceId))!.ExecutionBudgets);
            Assert.Null((await store.FindAsync(second.InstanceId))!.ExecutionBudgets);
            Assert.Equal("Conflict", (await Assert.ThrowsAsync<AgentCoreException>(() => store.UpdateWithExpectedRevisionAsync(new(first.InstanceId, 1, SetExecutionBudgets: true), now.AddSeconds(2)).AsTask())).Code);
            var inherited = await store.UpdateWithExpectedRevisionAsync(new(first.InstanceId, updated.Revision, SetExecutionBudgets: true), now.AddSeconds(2));
            Assert.Null(inherited.ExecutionBudgets);
        });
    }

    [Fact]
    public async Task Maintenance_pages_visit_every_instance_once_in_stable_id_order()
    {
        await ForEachStoresAsync(async store =>
        {
            var sample = SampleManaged(DateTimeOffset.Parse("2026-01-06T00:00:00Z"));
            for (var i = 101; i >= 1; i--) await store.InsertAsync(sample with { InstanceId = Guid.Parse($"aaaaaaaa-aaaa-aaaa-aaaa-{i:000000000000}") });
            var seen = new List<Guid>(); Guid? cursor = null;
            while (true)
            {
                var page = await store.ListMaintenancePageAsync(cursor, 40);
                if (page.Count == 0) break;
                Assert.InRange(page.Count, 1, 40);
                seen.AddRange(page.Select(i => i.InstanceId)); cursor = page[^1].InstanceId;
            }
            Assert.Equal(101, seen.Count); Assert.Equal(101, seen.Distinct().Count());
            Assert.Equal(seen.OrderBy(i => i.ToString("D"), StringComparer.Ordinal), seen);
        });
    }

    [Fact]
    public async Task Managed_update_with_expected_revision_succeeds_and_bumps_aggregate_revision()
    {
        await ForEachStoresAsync(async store =>
        {
            var now = DateTimeOffset.Parse("2026-01-06T00:00:00Z");
            var instance = SampleManaged(now);
            await store.InsertAsync(instance);
            var updated = await store.UpdateWithExpectedRevisionAsync(
                new AgentInstanceRevisionUpdate(instance.InstanceId, 1, ActiveVersion: 2),
                now.AddMinutes(1));
            Assert.Equal(2, updated.ActiveVersion);
            Assert.Equal(2, updated.Revision);
            Assert.Equal(1, updated.PersonaRevision);
        });
    }

    [Fact]
    public async Task Stale_expected_revision_is_rejected()
    {
        await ForEachStoresAsync(async store =>
        {
            var now = DateTimeOffset.Parse("2026-01-06T00:00:00Z");
            var instance = SampleManaged(now);
            await store.InsertAsync(instance);
            _ = await store.UpdateWithExpectedRevisionAsync(
                new AgentInstanceRevisionUpdate(instance.InstanceId, 1, ActiveVersion: 2),
                now.AddMinutes(1));
            var error = await Assert.ThrowsAsync<AgentCoreException>(() =>
                store.UpdateWithExpectedRevisionAsync(
                    new AgentInstanceRevisionUpdate(instance.InstanceId, 1, ActiveVersion: 3),
                    now.AddMinutes(2)).AsTask());
            Assert.Equal("Conflict", error.Code);
        });
    }

    [Fact]
    public async Task Persona_update_bumps_persona_revision_only()
    {
        await ForEachStoresAsync(async store =>
        {
            var now = DateTimeOffset.Parse("2026-01-06T00:00:00Z");
            var instance = SampleManaged(now);
            await store.InsertAsync(instance);
            var persona = instance.Persona with { Tone = "Updated" };
            var updated = await store.UpdateWithExpectedRevisionAsync(
                new AgentInstanceRevisionUpdate(instance.InstanceId, 1, Persona: persona, ExpectedPersonaRevision: 1),
                now.AddMinutes(1));
            Assert.Equal("Updated", updated.Persona.Tone);
            Assert.Equal(2, updated.Revision);
            Assert.Equal(2, updated.PersonaRevision);
            Assert.Equal(1, updated.ActiveVersion);
        });
    }

    [Fact]
    public async Task Persona_edit_without_expected_persona_revision_is_rejected()
    {
        await ForEachStoresAsync(async store =>
        {
            var now = DateTimeOffset.Parse("2026-01-06T00:00:00Z");
            var instance = SampleManaged(now);
            await store.InsertAsync(instance);
            var error = await Assert.ThrowsAsync<AgentCoreException>(() =>
                store.UpdateWithExpectedRevisionAsync(
                    new AgentInstanceRevisionUpdate(
                        instance.InstanceId,
                        1,
                        Persona: instance.Persona with { Tone = "Missing token" }),
                    now.AddMinutes(1)).AsTask());
            Assert.Equal("Validation", error.Code);
        });
    }

    [Fact]
    public async Task Stale_expected_persona_revision_is_rejected()
    {
        await ForEachStoresAsync(async store =>
        {
            var now = DateTimeOffset.Parse("2026-01-06T00:00:00Z");
            var instance = SampleManaged(now);
            await store.InsertAsync(instance);
            _ = await store.UpdateWithExpectedRevisionAsync(
                new AgentInstanceRevisionUpdate(
                    instance.InstanceId,
                    1,
                    Persona: instance.Persona with { Tone = "First" },
                    ExpectedPersonaRevision: 1),
                now.AddMinutes(1));
            var error = await Assert.ThrowsAsync<AgentCoreException>(() =>
                store.UpdateWithExpectedRevisionAsync(
                    new AgentInstanceRevisionUpdate(
                        instance.InstanceId,
                        2,
                        Persona: instance.Persona with { Tone = "Second" },
                        ExpectedPersonaRevision: 1),
                    now.AddMinutes(2)).AsTask());
            Assert.Equal("Conflict", error.Code);
        });
    }

    [Fact]
    public async Task Version_and_persona_interleaved_updates_reject_stale_aggregate_revision()
    {
        await ForEachStoresAsync(async store =>
        {
            var now = DateTimeOffset.Parse("2026-01-06T00:00:00Z");
            var instance = SampleManaged(now);
            await store.InsertAsync(instance);
            _ = await store.UpdateWithExpectedRevisionAsync(
                new AgentInstanceRevisionUpdate(instance.InstanceId, 1, ActiveVersion: 2),
                now.AddMinutes(1));
            var error = await Assert.ThrowsAsync<AgentCoreException>(() =>
                store.UpdateWithExpectedRevisionAsync(
                    new AgentInstanceRevisionUpdate(
                        instance.InstanceId,
                        1,
                        Persona: instance.Persona with { Tone = "Late persona" },
                        ExpectedPersonaRevision: 1),
                    now.AddMinutes(2)).AsTask());
            Assert.Equal("Conflict", error.Code);
        });
    }

    [Fact]
    public async Task Independent_instances_of_same_definition_are_mutable()
    {
        await ForEachStoresAsync(async store =>
        {
            var now = DateTimeOffset.Parse("2026-01-06T00:00:00Z");
            var first = SampleManaged(now);
            var second = SampleManaged(now);
            await store.InsertAsync(first);
            await store.InsertAsync(second);
            var updated = await store.UpdateWithExpectedRevisionAsync(
                new AgentInstanceRevisionUpdate(first.InstanceId, 1,
                    Persona: first.Persona with { Tone = "Warm" }, ExpectedPersonaRevision: 1), now.AddMinutes(1));
            Assert.Equal("Warm", updated.Persona.Tone);
            Assert.Equal(second.Persona, (await store.FindAsync(second.InstanceId))!.Persona);
        });
    }

    private static AgentInstance SampleManaged(DateTimeOffset now) =>
        new(
            Guid.CreateVersion7(),
            "examiner",
            1,
            new AgentIdentity("Demo", "Guide", "Helps.", "Calm"),
            AgentInstanceLifecycle.Active,
            now,
            now);
}

public sealed class InMemoryAgentInstanceStoreContractTests : AgentInstanceStoreContractTests
{
    protected override Task ForEachStoresAsync(Func<IAgentInstanceStore, Task> exercise) =>
        exercise(new InMemoryAgentInstanceStore());
}

[Collection("AgentInstanceSqliteSerial")]
public sealed class SqliteAgentInstanceStoreContractTests : AgentInstanceStoreContractTests
{
    [Fact]
    public async Task Concurrent_updates_from_same_revision_commit_once()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-instances-race-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        var factory = new SqliteContextFactory(options);
        try
        {
            await new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync();
            var store = new SqliteAgentInstanceStore(factory, new SystemIdGenerator(TimeProvider.System));
            var now = DateTimeOffset.Parse("2026-01-06T00:00:00Z");
            var instance = new AgentInstance(
                Guid.CreateVersion7(),
                "examiner",
                1,
                new AgentIdentity("Demo", "Guide", "Helps.", "Calm"),
                AgentInstanceLifecycle.Active,
                now,
                now);
            await store.InsertAsync(instance);
            var first = TryUpdateAsync(
                store,
                new AgentInstanceRevisionUpdate(instance.InstanceId, 1, ActiveVersion: 2),
                now.AddMinutes(1));
            var second = TryUpdateAsync(
                store,
                new AgentInstanceRevisionUpdate(instance.InstanceId, 1, ActiveVersion: 3),
                now.AddMinutes(1));
            var outcomes = await Task.WhenAll(first, second);
            Assert.Equal(1, outcomes.Count(success => success));
            var reloaded = await store.FindAsync(instance.InstanceId);
            Assert.NotNull(reloaded);
            Assert.Equal(2, reloaded.Revision);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static async Task<bool> TryUpdateAsync(
        IAgentInstanceStore store,
        AgentInstanceRevisionUpdate update,
        DateTimeOffset updatedAt)
    {
        try
        {
            await store.UpdateWithExpectedRevisionAsync(update, updatedAt);
            return true;
        }
        catch (AgentCoreException ex) when (ex.Code == "Conflict")
        {
            return false;
        }
    }

    protected override async Task ForEachStoresAsync(Func<IAgentInstanceStore, Task> exercise)
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-instances-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        var factory = new SqliteContextFactory(options);
        try
        {
            await new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync();
            await exercise(new SqliteAgentInstanceStore(factory, new SystemIdGenerator(TimeProvider.System)));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private sealed class SqliteContextFactory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    {
        public AgentCoreDbContext CreateDbContext() => new(options);

        public Task<AgentCoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
