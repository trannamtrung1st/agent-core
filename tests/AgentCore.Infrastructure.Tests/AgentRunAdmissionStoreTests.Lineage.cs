using System.Text.Json.Nodes;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class AgentRunAdmissionStoreTests
{
    [Fact]
    public async Task Root_migration_refuses_historical_execution_without_inventing_lineage_or_deleting_rows()
    {
        await using var source = await Fixture.CreateAsync(true);
        var turn = UserTurn();
        await source.Memory.SaveAsync(turn.Snapshot, 0);
        await using var historical = await HistoricalTenantDatabase.CreateAsync(source.Contexts!,
            "20261016233720_TenantSessionOwnership", ["Sessions", "SessionSnapshots", "ConversationEntries"]);
        var db = historical.Db;
        var a = turn.Run.ActivationId.ToString("D"); var session = turn.Snapshot.SessionId.ToString("D");
        var agent = Owner.AgentInstanceId.ToString("D"); var profile = Owner.ProfileId.ToString("D");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Activations (ActivationId, SessionId, DedupeKey, AdmissionHash, AgentInstanceId, ProfileId, PayloadJson, AdmittedAtUtc) VALUES ({a}, {session}, 'retained', 'retained', {agent}, {profile}, '{{}}', 0)");
        var error = await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync("20261017001916_ActivationRootLineage"));
        Assert.Contains("Execution_history_requires_explicit_reset", error.Message);
        Assert.Equal(1, await db.Activations.CountAsync());
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM Sessions").SingleAsync());
        Assert.Equal("20261016233720_TenantSessionOwnership", (await db.Database.GetAppliedMigrationsAsync()).Last());
    }

}
