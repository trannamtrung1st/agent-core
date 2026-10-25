using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Tests;

public sealed class ContextSuccessorMigrationTests
{
    [Fact]
    public async Task Context_successor_migration_changes_only_its_Core_kind_mapping_and_roundtrips_exact_predecessor()
    {
        var path = Path.Combine(Path.GetTempPath(), "context-successor-roundtrip-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            await using var db = new AgentCoreDbContext(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite("Data Source=" + path + ";Foreign Keys=True").Options);
            var migrator = db.GetService<IMigrator>(); await migrator.MigrateAsync("20261025050000_SpaceAutomationPins");
            async Task<Dictionary<string, string>> Guards()
            {
                await db.Database.OpenConnectionAsync(); await using var query = db.Database.GetDbConnection().CreateCommand();
                query.CommandText = "SELECT name,sql FROM sqlite_master WHERE type='trigger' ORDER BY name";
                var result = new Dictionary<string, string>(); await using var reader = await query.ExecuteReaderAsync();
                while (await reader.ReadAsync()) if (!reader.GetString(0).StartsWith("TR_SpaceResources_", StringComparison.Ordinal) && !reader.GetString(0).StartsWith("TR_SpaceBinding_Source_", StringComparison.Ordinal) && !reader.GetString(0).StartsWith("TR_SpaceSources_", StringComparison.Ordinal)) result.Add(reader.GetString(0), reader.GetString(1)); return result;
            }
            var previous = await Guards();
            async Task AssertCurrent()
            {
                var current = await Guards(); Assert.Equal(previous.Count + 2, current.Count);
                foreach (var item in previous)
                    Assert.Equal(item.Key == "TR_TenantCore_Fact_Insert"
                        ? item.Value.Replace("WHEN 9 THEN 'ApprovalContinuation' END", "WHEN 9 THEN 'ApprovalContinuation' WHEN 10 THEN 'ContextContinuation' END", StringComparison.Ordinal)
                        : item.Value, current[item.Key]);
                Assert.Contains("exact completed turn and saved selection", current["TR_TenantRuns_Context_INSERT"]);
                Assert.Contains("immutable context continuation", current["TR_TenantRuns_Context_UPDATE"]);
                Assert.False(db.Database.HasPendingModelChanges());
                await using var command = db.Database.GetDbConnection().CreateCommand(); command.CommandText = "PRAGMA foreign_key_check";
                await using var reader = await command.ExecuteReaderAsync(); Assert.False(await reader.ReadAsync());
            }
            await migrator.MigrateAsync(); await AssertCurrent();
            await migrator.MigrateAsync("20261025050000_SpaceAutomationPins"); Assert.Equal(previous, await Guards());
            await migrator.MigrateAsync(); await AssertCurrent();
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(path); File.Delete(path + "-wal"); File.Delete(path + "-shm"); }
    }
}
