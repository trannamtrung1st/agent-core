using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Tests;
public sealed class TenantManagementResourceMigrationTests
{
    [Fact]
    public async Task Resource_authoring_migration_preserves_incumbent_guards_indexes_and_exact_roundtrip_with_a_current_reopen()
    {
        var path = Path.Combine(Path.GetTempPath(), "resource-authoring-migration-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite("Data Source=" + path + ";Foreign Keys=True").Options;
            await using (var db = new AgentCoreDbContext(options))
            {
                var migrator = db.GetService<IMigrator>(); await migrator.MigrateAsync("20261022120000_TenantPrivateSkillActivation");
                var before = await Definitions(db, "trigger"); var indexes = await Definitions(db, "index"); Assert.Equal(121, before.Count);
                await migrator.MigrateAsync("20261023100000_TenantManagementResourceAuthoring"); var after = await Definitions(db, "trigger"); Assert.Equal(124, after.Count);
                foreach (var guard in before) Assert.Equal(guard.Key == "TR_TenantOperationReceipts_ManagementMutation_INSERT"
                    ? guard.Value.Replace("AND NEW.ManagementSkillJson IS NULL AND (NEW.Outcome", "AND NEW.ManagementSkillJson IS NULL AND NEW.ManagementResourceJson IS NULL AND (NEW.Outcome")
                    : guard.Value, after[guard.Key]);
                Assert.Equal(indexes, await Definitions(db, "index")); Assert.False(db.Database.HasPendingModelChanges());
                await migrator.MigrateAsync("20261022120000_TenantPrivateSkillActivation");
                Assert.Equal(before, await Definitions(db, "trigger")); Assert.Equal(indexes, await Definitions(db, "index"));
                await migrator.MigrateAsync("20261023100000_TenantManagementResourceAuthoring"); Assert.Equal(after, await Definitions(db, "trigger")); Assert.False(db.Database.HasPendingModelChanges());
            }
            await using var reopened = new AgentCoreDbContext(options); await reopened.Database.MigrateAsync(); Assert.False(reopened.Database.HasPendingModelChanges());
            await reopened.Database.OpenConnectionAsync(); using var check = reopened.Database.GetDbConnection().CreateCommand();
            check.CommandText = "PRAGMA foreign_key_check"; using var rows = await check.ExecuteReaderAsync(); Assert.False(await rows.ReadAsync());
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(path); File.Delete(path + "-wal"); File.Delete(path + "-shm"); }
    }
    private static async Task<SortedDictionary<string,string>> Definitions(AgentCoreDbContext db, string type)
    {
        await db.Database.OpenConnectionAsync(); using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT name,sql FROM sqlite_master WHERE type=$type AND sql IS NOT NULL ORDER BY name";
        var parameter = command.CreateParameter(); parameter.ParameterName = "$type"; parameter.Value = type; command.Parameters.Add(parameter);
        using var rows = await command.ExecuteReaderAsync(); var result = new SortedDictionary<string,string>();
        while (await rows.ReadAsync()) result.Add(rows.GetString(0), rows.GetString(1)); return result;
    }
}
