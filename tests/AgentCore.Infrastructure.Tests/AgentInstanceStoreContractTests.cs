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
    public async Task Compatibility_instance_rejects_persona_and_lifecycle_mutations()
    {
        await ForEachStoresAsync(async store =>
        {
            var now = DateTimeOffset.Parse("2026-01-06T00:00:00Z");
            var compat = SampleManaged(now) with
            {
                InstanceId = AgentInstance.CompatibilityFor("examiner"),
                Compatibility = true
            };
            await store.InsertAsync(compat);
            var error = await Assert.ThrowsAsync<AgentCoreException>(() =>
                store.UpdateWithExpectedRevisionAsync(
                    new AgentInstanceRevisionUpdate(
                        compat.InstanceId,
                        1,
                        Persona: compat.Persona with { Tone = "nope" }),
                    now.AddMinutes(1)).AsTask());
            Assert.Equal("Validation", error.Code);
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
            now,
            Compatibility: false);
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
                now,
                Compatibility: false);
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
