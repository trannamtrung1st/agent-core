using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Tests;

public sealed class TenantAutomationConversationMigrationTests
{
    [Fact]
    public async Task Conversation_destination_guards_roundtrip_without_rewriting_incumbent_owners_or_destinations()
    {
        var path = Path.Combine(Path.GetTempPath(), "tenant-conversation-migration-" + Guid.NewGuid().ToString("N") + ".db");
        var factory = new Factory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite("Data Source=" + path + ";Foreign Keys=True").Options);
        try
        {
            await using var db = factory.CreateDbContext(); await db.GetService<IMigrator>().MigrateAsync("20261021050000_TenantAutomationCompletionDestination");
            var previous = await Guards(db); Assert.Equal(79, previous.Length);
            var at = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var schedule = new OneShotSchedule(at.AddDays(1), "UTC");
            var legacy = new Automation(Guid.NewGuid(), new(Guid.NewGuid(), Guid.NewGuid()), AutomationStatus.Active, "Retain exact legacy report destination", new ScheduleTrigger(schedule), schedule.AtUtc,
                null, 0, 1, 1, new(TriggerAuthorizationOrigin.AdminOwner, null, null, at, at), null,
                executionTarget: AutomationExecutionTarget.Existing(Guid.NewGuid()));
            var store = new SqliteTriggerStore(factory); await store.CreateAsync(legacy);
            await db.Database.MigrateAsync(); Assert.False(db.Database.HasPendingModelChanges());
            var latest = await Guards(db); Assert.Equal(83, latest.Length); Assert.All(previous, name => Assert.Contains(name, latest));
            Assert.Contains("TR_TenantConversation_Automation_Insert", latest); Assert.Contains("TR_TenantConversation_Occurrence_Update", latest);
            Assert.Equal(legacy.ExecutionTarget, (await store.GetAsync(legacy.Owner, legacy.AutomationId))!.ExecutionTarget);
            await db.GetService<IMigrator>().MigrateAsync("20261021050000_TenantAutomationCompletionDestination"); Assert.Equal(previous, await Guards(db));
            await db.Database.MigrateAsync(); Assert.Equal(latest, await Guards(db)); Assert.False(db.Database.HasPendingModelChanges());
            Assert.Equal(legacy.ExecutionTarget, (await store.GetAsync(legacy.Owner, legacy.AutomationId))!.ExecutionTarget);
            await db.Database.OpenConnectionAsync(); using var check = db.Database.GetDbConnection().CreateCommand(); check.CommandText = "PRAGMA foreign_key_check";
            using var rows = await check.ExecuteReaderAsync(); Assert.False(await rows.ReadAsync());
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(path); File.Delete(path + "-wal"); File.Delete(path + "-shm"); }
    }
    private static async Task<string[]> Guards(AgentCoreDbContext db)
    {
        await db.Database.OpenConnectionAsync(); using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='trigger' ORDER BY name";
        using var rows = await command.ExecuteReaderAsync(); var names = new List<string>(); while (await rows.ReadAsync()) names.Add(rows.GetString(0)); return names.ToArray();
    }
    private sealed class Factory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    { public AgentCoreDbContext CreateDbContext() => new(options); }
}
