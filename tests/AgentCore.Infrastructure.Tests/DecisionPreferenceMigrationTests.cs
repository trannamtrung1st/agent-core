using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Tests;

public sealed class DecisionPreferenceMigrationTests
{
    [Fact]
    public async Task Offline_cutover_preserves_guards_and_converts_only_mutable_fixed_intent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new AgentCoreDbContext(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite(connection).Options);
        await db.GetService<IMigrator>().MigrateAsync("20261026090000_DeepSeekFlash1017");
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO AgentInstances(InstanceId,DefinitionId,ActiveVersion,PersonaJson,Lifecycle,CreatedAtUtc,UpdatedAtUtc,Revision,PersonaRevision,UnattendedOfferingId,UnattendedReasoningEffort)
            VALUES ('dca30000-0000-4000-8000-000000000099','examiner',1,'{{"name":"Migrated","role":"Examiner","description":"Test","tone":"Calm"}}','Active',0,0,1,1,'dca30000-0000-4000-8000-000000000001','low');
            """);
        var before = await Guards(connection);
        await db.GetService<IMigrator>().MigrateAsync("20261026100000_DecisionIntelligencePreferences");
        var after = await Guards(connection);
        Assert.Equal(before.Select(v => v.Split(':')[0]), after.Select(v => v.Split(':')[0]));
        Assert.Equal(188, before.Intersect(after).Count()); // Only the two preference-bearing guards change.
        Assert.DoesNotContain(after, sql => sql.Contains("UnattendedOfferingId") || sql.Contains("UnattendedReasoningEffort"));
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT json_extract(UnattendedModelJson,'$.mode'),json_extract(UnattendedModelJson,'$.offeringId'),json_extract(UnattendedModelJson,'$.reasoning.effort') FROM AgentInstances";
        await using var values = await command.ExecuteReaderAsync(); Assert.True(await values.ReadAsync());
        Assert.Equal("fixed", values.GetString(0)); Assert.Equal("dca30000-0000-4000-8000-000000000001", values.GetString(1)); Assert.Equal("low", values.GetString(2));
        await values.DisposeAsync();
        await db.Database.MigrateAsync(); Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Unsupported_predecessor_refuses_atomically_without_dropping_old_fields()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new AgentCoreDbContext(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite(connection).Options);
        await db.GetService<IMigrator>().MigrateAsync("20261026090000_DeepSeekFlash1017");
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO AgentInstances(InstanceId,DefinitionId,ActiveVersion,PersonaJson,Lifecycle,CreatedAtUtc,UpdatedAtUtc,Revision,PersonaRevision,UnattendedReasoningEffort)
            VALUES ('dca30000-0000-4000-8000-000000000099','examiner',1,'{{}}','Active',0,0,1,1,'low');
            """);
        var error = await Assert.ThrowsAsync<SqliteException>(() => db.Database.MigrateAsync());
        Assert.Contains("DecisionPreferenceRequiresBackedUpFreshState", error.Message);
        Assert.Equal("20261026090000_DeepSeekFlash1017", (await db.Database.GetAppliedMigrationsAsync()).Last());
        using var command = connection.CreateCommand(); command.CommandText = "SELECT UnattendedReasoningEffort FROM AgentInstances";
        Assert.Equal("low", await command.ExecuteScalarAsync());
    }

    private static async Task<string[]> Guards(SqliteConnection connection)
    {
        using var command = connection.CreateCommand(); command.CommandText = "SELECT name||':'||sql FROM sqlite_master WHERE type='trigger' ORDER BY name";
        await using var rows = await command.ExecuteReaderAsync(); var result = new List<string>();
        while (await rows.ReadAsync()) result.Add(rows.GetString(0));
        return result.ToArray();
    }
}
