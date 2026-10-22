using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Tests;
public sealed class TenantAutomationMutationMigrationTests
{
    [Fact]
    public async Task Automation_mutation_migration_preserves_all_incumbent_guards_indexes_and_current_model_on_roundtrip()
    {
        var path = Path.Combine(Path.GetTempPath(), "automation-mutation-migration-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            await using var db = new AgentCoreDbContext(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite("Data Source=" + path + ";Foreign Keys=True").Options);
            var migrations = db.GetService<IMigrator>(); await migrations.MigrateAsync("20261022020346_TenantAutomationReadConsent");
            var guards = await Definitions(db, "trigger"); Assert.Equal(106, guards.Count); var indexes = await Definitions(db, "index");
            await migrations.MigrateAsync("20261022041619_TenantAutomationMutations");
            var upgraded = await Definitions(db, "trigger"); Assert.Equal(110, upgraded.Count); foreach (var guard in guards) Assert.Equal(guard.Value, upgraded[guard.Key]);
            Assert.Equal(indexes, await Definitions(db, "index"));
            await migrations.MigrateAsync("20261022020346_TenantAutomationReadConsent"); Assert.Equal(guards, await Definitions(db, "trigger"));
            await migrations.MigrateAsync("20261022041619_TenantAutomationMutations");  Assert.Equal(upgraded, await Definitions(db, "trigger"));
            using var check = db.Database.GetDbConnection().CreateCommand(); check.CommandText = "PRAGMA foreign_key_check";
            using var result = await check.ExecuteReaderAsync(); Assert.False(await result.ReadAsync());
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(path); File.Delete(path + "-wal"); File.Delete(path + "-shm"); }
    }
    private static async Task<SortedDictionary<string,string>> Definitions(AgentCoreDbContext db, string type)
    {
        await db.Database.OpenConnectionAsync(); using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT name,sql FROM sqlite_master WHERE type=$type AND sql IS NOT NULL ORDER BY name";
        var parameter = command.CreateParameter(); parameter.ParameterName = "$type"; parameter.Value = type; command.Parameters.Add(parameter);
        using var rows = await command.ExecuteReaderAsync(); var values = new SortedDictionary<string,string>();
        while (await rows.ReadAsync()) values.Add(rows.GetString(0), rows.GetString(1)); return values;
    }
}
