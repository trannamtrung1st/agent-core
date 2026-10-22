using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Tests;

public sealed class TenantHarnessCommandMigrationTests
{
    [Fact]
    public async Task Harness_command_migration_preserves_prior_effect_schema_and_guards()
    {
        var path = Path.Combine(Path.GetTempPath(), "harness-migration-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite("Data Source=" + path + ";Foreign Keys=True").Options;
            await using var db = new AgentCoreDbContext(options);
            await db.GetService<IMigrator>().MigrateAsync("20261023100000_TenantManagementResourceAuthoring");
            await db.Database.OpenConnectionAsync();
            using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT type,name,sql FROM sqlite_master WHERE sql IS NOT NULL ORDER BY type,name";
            var schema = new Dictionary<string, string>();
            using (var rows = await command.ExecuteReaderAsync()) while (await rows.ReadAsync()) schema.Add(rows.GetString(0) + ":" + rows.GetString(1), rows.GetString(2));
            Assert.Equal(124, schema.Count(i => i.Key.StartsWith("trigger:", StringComparison.Ordinal)));
            var before = new Dictionary<string,string>(schema);
            await db.GetService<IMigrator>().MigrateAsync();
            Assert.False(db.Database.HasPendingModelChanges());
            var after = await Schema(db); Assert.Equal(127, after.Count(i => i.Key.StartsWith("trigger:", StringComparison.Ordinal)));
            foreach (var guard in before.Where(i => i.Key.StartsWith("trigger:", StringComparison.Ordinal)))
                if (guard.Key != "trigger:TR_TenantOperationReceipts_ManagementResource_INSERT") Assert.Equal(guard.Value, after[guard.Key]);
            Assert.Equal(before.Where(i => i.Key.StartsWith("index:")).OrderBy(i => i.Key), after.Where(i => i.Key.StartsWith("index:")).OrderBy(i => i.Key));
            await db.GetService<IMigrator>().MigrateAsync("20261023100000_TenantManagementResourceAuthoring");
            Assert.Equal(before.OrderBy(i => i.Key), (await Schema(db)).OrderBy(i => i.Key));
            await db.GetService<IMigrator>().MigrateAsync(); Assert.Equal(after.OrderBy(i => i.Key), (await Schema(db)).OrderBy(i => i.Key));
            using var check = db.Database.GetDbConnection().CreateCommand(); check.CommandText = "PRAGMA foreign_key_check";
            using var violations = await check.ExecuteReaderAsync(); Assert.False(await violations.ReadAsync());
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(path); File.Delete(path + "-wal"); File.Delete(path + "-shm"); }
    }
    private static async Task<Dictionary<string,string>> Schema(AgentCoreDbContext db)
    {
        await db.Database.OpenConnectionAsync(); using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT type,name,sql FROM sqlite_master WHERE sql IS NOT NULL ORDER BY type,name";
        using var rows = await command.ExecuteReaderAsync(); var result = new Dictionary<string,string>();
        while (await rows.ReadAsync()) result.Add(rows.GetString(0) + ":" + rows.GetString(1), rows.GetString(2)); return result;
    }
}
