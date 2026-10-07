using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Tests;

public sealed class UnifiedAutomationMigrationTests
{
    [Fact]
    public async Task Cutover_resets_disposable_behavior_and_runs_preserves_independent_owners_and_reopens()
    {
        var path = Path.Combine(Path.GetTempPath(), $"automation-migration-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        string[] reset = ["TriggerRegistrations", "ContinuityMaintenanceSettings", "WorkAttentionAlerts", "WorkApprovals",
            "WorkCaptures", "WorkItems", "TriggerOccurrences", "ExternalEventDeliveries", "ExternalEvents", "Experiences"];
        try
        {
            await using (var db = new AgentCoreDbContext(options))
            {
                await db.GetService<IMigrator>().MigrateAsync("20261007045013_SystemCredentials");
                await db.Database.OpenConnectionAsync();
                var connection = (SqliteConnection)db.Database.GetDbConnection();
                // Populate every disposable table using the old schema, including
                // Thought/Retrospection origin values. No new DTO reads old rows.
                foreach (var table in new[] { "AgentInstances", "UserProfiles", "TriggerRegistrations", "ContinuityMaintenanceSettings", "TriggerOccurrences", "WorkItems", "WorkApprovals", "WorkAttentionAlerts", "WorkCaptures", "ExternalEvents", "ExternalEventDeliveries", "Experiences" })
                {
                    await using var shape = connection.CreateCommand();
                    shape.CommandText = $"PRAGMA table_info(\"{table}\")";
                    var required = new List<(string Name, string Type)>();
                    await using (var reader = await shape.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                            if (reader.GetInt32(3) != 0 || reader.GetInt32(5) != 0)
                                required.Add((reader.GetString(1), reader.GetString(2)));
                    }
                    await using var seed = connection.CreateCommand();
                    seed.CommandText = $"INSERT INTO \"{table}\" ({string.Join(",", required.Select(c => $"\"{c.Name}\""))}) VALUES ({string.Join(",", required.Select((_, i) => "$v" + i))})";
                    for (var i = 0; i < required.Count; i++)
                    {
                        var column = required[i];
                        object value = column.Type == "INTEGER" ? 1 : column.Name.EndsWith("Json", StringComparison.Ordinal) ? "{}" : "sentinel";
                        if (column.Name == "SourceKind") value = 3;
                        if (column.Name == "AuthorizationOrigin") value = 1;
                        seed.Parameters.AddWithValue("$v" + i, value);
                    }
                    await seed.ExecuteNonQueryAsync();
                }
                await db.GetService<IMigrator>().MigrateAsync();
                foreach (var table in reset.Skip(2).Concat(["Automations"]))
                {
                    await using var count = connection.CreateCommand(); count.CommandText = $"SELECT COUNT(*) FROM \"{table}\"";
                    Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
                }
                await using var retired = connection.CreateCommand();
                retired.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('TriggerRegistrations','ContinuityMaintenanceSettings')";
                Assert.Equal(0L, (long)(await retired.ExecuteScalarAsync())!);
                Assert.Equal("sentinel", (await db.AgentInstances.SingleAsync()).InstanceId);
                Assert.Equal("sentinel", (await db.Profiles.SingleAsync()).ProfileId);
            }
            await using var reopened = new AgentCoreDbContext(options);
            await reopened.Database.MigrateAsync();
            Assert.Empty(await reopened.Automations.ToListAsync());
            Assert.Single(await reopened.AgentInstances.ToListAsync());
            Assert.Single(await reopened.Profiles.ToListAsync());
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(path); }
    }
}
