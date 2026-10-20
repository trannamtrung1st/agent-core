using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Tests;

public sealed class TenantAutomationMigrationTests
{
    [Fact]
    public async Task Tagged_Automation_migration_roundtrips_legacy_rows_and_restores_existing_security_guards()
    {
        var path = Path.Combine(Path.GetTempPath(), "tenant-automation-migration-" + Guid.NewGuid().ToString("N") + ".db");
        var factory = new Factory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite("Data Source=" + path + ";Foreign Keys=True").Options);
        try
        {
            await using var db = factory.CreateDbContext(); await db.Database.MigrateAsync();
            Assert.False(db.Database.HasPendingModelChanges());
            var at = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var schedule = new OneShotSchedule(at.AddDays(1), "UTC");
            var legacy = new Automation(Guid.NewGuid(), new(Guid.NewGuid(), Guid.NewGuid()), AutomationStatus.Active, "Retain legacy source bytes", new ScheduleTrigger(schedule), schedule.AtUtc,
                null, 0, 1, 1, new(TriggerAuthorizationOrigin.AdminOwner, null, null, at, at), null);
            await new SqliteTriggerStore(factory).CreateAsync(legacy);
            var guards = await Guards(db); Assert.Contains("TR_TenantOccurrences_Source_Insert", guards); Assert.Contains("TR_TenantSessions_Creator_Insert", guards);
            await db.GetService<IMigrator>().MigrateAsync("20261020170000_TenantAccountOperationRead");
            var previousGuards = await Guards(db); Assert.Equal(guards.Length - 5, previousGuards.Length);
            await db.Database.MigrateAsync(); Assert.Equal(guards, await Guards(db));
            var read = (await new SqliteTriggerStore(factory).GetAsync(legacy.Owner, legacy.AutomationId))!;
            Assert.Equal(legacy.Owner, read.Owner); Assert.Null(read.Owner.TenantId); Assert.Equal(legacy.Instructions, read.Instructions);
            Assert.Equal(legacy.NextOccurrenceAtUtc, read.NextOccurrenceAtUtc); Assert.Equal(legacy.Triggers.Single().TriggerId, read.Triggers.Single().TriggerId);
            await db.Database.OpenConnectionAsync(); using var command = db.Database.GetDbConnection().CreateCommand(); command.CommandText = "PRAGMA foreign_key_check";
            using var rows = await command.ExecuteReaderAsync(); Assert.False(await rows.ReadAsync());
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(path); File.Delete(path + "-wal"); File.Delete(path + "-shm"); }
    }
    private static async Task<string[]> Guards(AgentCoreDbContext db)
    {
        await db.Database.OpenConnectionAsync(); using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'trigger' ORDER BY name";
        using var reader = await command.ExecuteReaderAsync(); var names = new List<string>(); while (await reader.ReadAsync()) names.Add(reader.GetString(0)); return names.ToArray();
    }
    private sealed class Factory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    { public AgentCoreDbContext CreateDbContext() => new(options); }
}
