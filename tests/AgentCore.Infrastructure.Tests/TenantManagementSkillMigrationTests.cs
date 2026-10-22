using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Tests;
public sealed class TenantManagementSkillMigrationTests
{
    [Fact]
    public async Task Skill_authoring_migration_preserves_incumbent_guards_and_indexes_and_roundtrips_the_exact_current_model()
    {
        var path = Path.Combine(Path.GetTempPath(), "management-skill-migration-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite("Data Source=" + path + ";Foreign Keys=True").Options;
            await using (var db = new AgentCoreDbContext(options))
            {
                var migrations = db.GetService<IMigrator>(); await migrations.MigrateAsync("20261022100000_TenantManagementChildReads");
                var guards = await Definitions(db, "trigger"); Assert.Equal(117, guards.Count); var indexes = await Definitions(db, "index");
                await migrations.MigrateAsync("20261022110000_TenantManagementSkillAuthoring");
                var upgraded = await Definitions(db, "trigger"); Assert.Equal(119, upgraded.Count);
                foreach (var guard in guards)
                    Assert.Equal(guard.Key == "TR_TenantOperationReceipts_ManagementMutation_INSERT"
                        ? guard.Value.Replace("WHEN NEW.ReasonCode='management_mutation_completed' AND (NEW.Outcome", "WHEN NEW.ReasonCode='management_mutation_completed' AND NEW.ManagementSkillJson IS NULL AND (NEW.Outcome") : guard.Value,
                        upgraded[guard.Key]);
                Assert.Equal(indexes, await Definitions(db, "index"));
                await migrations.MigrateAsync("20261022100000_TenantManagementChildReads");
                Assert.Equal(guards, await Definitions(db, "trigger")); Assert.Equal(indexes, await Definitions(db, "index"));
                await migrations.MigrateAsync("20261022110000_TenantManagementSkillAuthoring"); Assert.Equal(upgraded, await Definitions(db, "trigger"));
                await migrations.MigrateAsync(); Assert.False(db.Database.HasPendingModelChanges());
            }
            await using var reopened = new AgentCoreDbContext(options);
            Assert.False(reopened.Database.HasPendingModelChanges());
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
        using var rows = await command.ExecuteReaderAsync(); var values = new SortedDictionary<string,string>();
        while (await rows.ReadAsync()) values.Add(rows.GetString(0), rows.GetString(1)); return values;
    }
}
