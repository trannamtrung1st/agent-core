using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Tests;

public sealed class UnifiedAutomationMigrationTests
{
    [Fact]
    public async Task Trigger_revision_column_rename_preserves_automation_and_occurrence_values_and_reopens()
    {
        var path = Path.Combine(Path.GetTempPath(), $"automation-revision-migration-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        var preserved = new Dictionary<string, string>();
        try
        {
            await using (var db = new AgentCoreDbContext(options))
            {
                await db.GetService<IMigrator>().MigrateAsync("20261007072939_UnifiedAutomation");
                await db.Database.OpenConnectionAsync();
                var connection = (SqliteConnection)db.Database.GetDbConnection();
                foreach (var table in new[] { "Automations", "TriggerOccurrences" })
                {
                    await SeedRequiredColumnsAsync(connection, table);
                    if (table == "Automations")
                    {
                        await using var valid = connection.CreateCommand();
                        valid.CommandText = "UPDATE Automations SET TriggerKind = 0, ScheduleJson = '{}'";
                        await valid.ExecuteNonQueryAsync();
                    }
                    await using var revision = connection.CreateCommand();
                    var revisionColumn = table == "Automations" ? "Revision" : "RoutingRevision";
                    revision.CommandText = $"UPDATE \"{table}\" SET ScheduleRevision = 7, {revisionColumn} = 11";
                    await revision.ExecuteNonQueryAsync();
                    preserved[table] = await ReadRowAsync(connection, table, oldSchema: true);
                }
                await db.GetService<IMigrator>().MigrateAsync();
                await using (var defaults = connection.CreateCommand())
                {
                    defaults.CommandText = "PRAGMA table_info('Automations');";
                    await using var reader = await defaults.ExecuteReaderAsync();
                    var found = false;
                    while (await reader.ReadAsync())
                        if (reader.GetString(1) == "DispatchMode") { Assert.Equal("0", reader.GetString(4)); found = true; }
                    Assert.True(found);
                }
                foreach (var table in preserved.Keys)
                    Assert.Equal(preserved[table], await ReadRowAsync(connection, table, oldSchema: false));
            }
            await using var reopened = new AgentCoreDbContext(options);
            await reopened.Database.MigrateAsync();
            Assert.False(reopened.Database.HasPendingModelChanges());
            await reopened.Database.OpenConnectionAsync();
            foreach (var table in preserved.Keys)
                Assert.Equal(preserved[table], await ReadRowAsync((SqliteConnection)reopened.Database.GetDbConnection(), table, oldSchema: false));
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(path); }
    }

    private static async Task<string> ReadRowAsync(SqliteConnection connection, string table, bool oldSchema)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM \"{table}\"";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var values = new SortedDictionary<string, object?>();
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var name = reader.GetName(i);
            Assert.NotEqual(oldSchema ? "TriggerRevision" : "ScheduleRevision", name);
            if (name == "EventType") continue;
            if (name is "CoreEventKey" or "FilterExpression" or "DispatchMode" or "DispatchWindowSeconds" or "PresetId" or "PresetVersion")
            { Assert.True(name == "DispatchMode" ? reader.GetInt32(i) == 0 : reader.IsDBNull(i)); continue; }
            if (name is "ExecutionTargetKind" or "TargetSessionId" or "ReportToSessionId" or "RequiresTools")
            {
                Assert.True(name is "ExecutionTargetKind" or "RequiresTools" ? reader.GetInt32(i) == 0 : reader.IsDBNull(i));
                continue;
            }
            if (name is "ExecutionSessionId" or "AcceptedAgentRunId" or "LiveSessionId" or "LiveEvaluationCompletedAtUtc" or "DurableWorkItemId")
            {
                // The later admission schema adds nullable links without converting old receipts.
                Assert.True(reader.IsDBNull(i));
                continue;
            }
            values[oldSchema && name == "ScheduleRevision" ? "TriggerRevision" : name == "EventSourceId" ? "EventId" : name] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        }
        Assert.Equal(7L, values["TriggerRevision"]);
        Assert.Equal(11L, values[table == "Automations" ? "Revision" : "RoutingRevision"]);
        Assert.False(await reader.ReadAsync());
        return System.Text.Json.JsonSerializer.Serialize(values);
    }

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
                    await SeedRequiredColumnsAsync(connection, table);
                }
                await db.GetService<IMigrator>().MigrateAsync();
                foreach (var table in new[] { "TriggerOccurrences", "ExternalEventDeliveries", "ExternalEvents", "Experiences", "Automations" })
                {
                    await using var count = connection.CreateCommand(); count.CommandText = $"SELECT COUNT(*) FROM \"{table}\"";
                    Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
                }
                await using var retired = connection.CreateCommand();
                retired.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('TriggerRegistrations','ContinuityMaintenanceSettings','ConversationTurnExecutions','WorkItems','WorkApprovals','WorkAttentionAlerts','WorkCaptures')";
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

    private static async Task SeedRequiredColumnsAsync(SqliteConnection connection, string table)
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
}
