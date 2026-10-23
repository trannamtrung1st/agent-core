using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class AgentRunAdmissionStoreTests
{
    private const string BeforeEmbeddedRetirement = "20261023140000_TenantConfigurationRemedyAuthoring";

    [Fact]
    public async Task Embedded_approval_migration_removes_only_empty_state_and_preserves_run_across_reopen()
    {
        await using var f = await Fixture.CreateAsync(true);
        var turn = UserTurn(); await f.Runs.AdmitAsync(turn.Snapshot, 0, turn.Run);
        await using (var db = await f.Contexts!.CreateDbContextAsync())
        {
            var migrator = db.GetService<IMigrator>(); await migrator.MigrateAsync(BeforeEmbeddedRetirement);
            await db.Database.ExecuteSqlRawAsync("UPDATE AgentRuns SET PayloadJson=json_set(PayloadJson,'$.approval',NULL)");
            await migrator.MigrateAsync();
            Assert.False(db.Database.HasPendingModelChanges());
            var row = await db.AgentRuns.SingleAsync();
            using var payload = System.Text.Json.JsonDocument.Parse(row.PayloadJson);
            Assert.False(payload.RootElement.TryGetProperty("approval", out _));
            Assert.Equal(turn.Run.Revision, row.Revision);
            await db.Database.OpenConnectionAsync();
            using var columns = db.Database.GetDbConnection().CreateCommand();
            columns.CommandText = "SELECT count(*) FROM pragma_table_info('AgentRuns') WHERE name='ApprovalExpiresAtUtc'";
            Assert.Equal(0L, await columns.ExecuteScalarAsync());
            var injected = await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync(
                "UPDATE AgentRuns SET PayloadJson=json_set(PayloadJson,'$.approval',NULL)"));
            Assert.Contains("embedded Run approvals are retired", injected.Message);
            await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("UPDATE AgentRuns SET Status=2"));
        }
        await f.ReopenAsync();
        var retained = (await f.Runs.GetAsync(Owner, turn.Run.AgentRunId))!;
        Assert.Equal(turn.Run.ActivationId, retained.ActivationId);
        Assert.Equal(turn.Run.Admission.Activation.RootActivationId, retained.Admission.Activation.RootActivationId);
        Assert.Equal(turn.Run.Admission.Activation.SourceEntryIds, retained.Admission.Activation.SourceEntryIds);
        Assert.Equal(turn.Run.Revision, retained.Revision);
        Assert.Null(retained.ApprovalRequestId);
        Assert.Equal(AgentRunStatus.Queued, retained.Status);
        var running = await Claim(f, retained);
        Assert.Equal(1, running.AttemptCount);
    }

    [Theory]
    [InlineData("waiting")]
    [InlineData("record")]
    [InlineData("expiry")]
    public async Task Embedded_approval_migration_refuses_actual_legacy_state_without_altering_rows(string state)
    {
        await using var f = await Fixture.CreateAsync(true);
        var turn = UserTurn(); await f.Runs.AdmitAsync(turn.Snapshot, 0, turn.Run);
        await using var db = await f.Contexts!.CreateDbContextAsync();
        var migrator = db.GetService<IMigrator>(); await migrator.MigrateAsync(BeforeEmbeddedRetirement);
        if (state == "waiting") await db.Database.ExecuteSqlRawAsync("UPDATE AgentRuns SET Status=2");
        else if (state == "expiry") await db.Database.ExecuteSqlRawAsync("UPDATE AgentRuns SET ApprovalExpiresAtUtc=123456");
        else await db.Database.ExecuteSqlRawAsync("UPDATE AgentRuns SET PayloadJson=json_set(PayloadJson,'$.approval',json_object('decision',1))");
        var before = await db.AgentRuns.AsNoTracking().SingleAsync();
        var guards = await EmbeddedRunGuards(db);
        var applied = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
        var error = await Assert.ThrowsAsync<SqliteException>(() => migrator.MigrateAsync());
        Assert.Contains("RequiresExplicitLegacyApprovalRecovery", error.Message);
        var after = await db.AgentRuns.AsNoTracking().SingleAsync();
        Assert.Equal(before.PayloadJson, after.PayloadJson); Assert.Equal(before.Status, after.Status); Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(applied, await db.Database.GetAppliedMigrationsAsync());
        Assert.Equal(guards, await EmbeddedRunGuards(db));
    }

    private static async Task<SortedDictionary<string, string>> EmbeddedRunGuards(AgentCoreDbContext db)
    {
        await db.Database.OpenConnectionAsync(); using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT name,sql FROM sqlite_master WHERE type='trigger' AND tbl_name='AgentRuns' ORDER BY name";
        using var rows = await command.ExecuteReaderAsync(); var guards = new SortedDictionary<string, string>();
        while (await rows.ReadAsync()) guards.Add(rows.GetString(0), rows.GetString(1));
        return guards;
    }
}
