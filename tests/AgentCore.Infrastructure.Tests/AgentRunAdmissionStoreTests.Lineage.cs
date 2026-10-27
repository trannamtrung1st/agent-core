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
        var path = Path.Combine(Path.GetTempPath(), $"activation-root-refusal-{Guid.NewGuid():N}.db");
        var factory = new Factory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options);
        try
        {
            await using var db = factory.CreateDbContext();
            await db.GetService<IMigrator>().MigrateAsync("20261016233720_TenantSessionOwnership");
            var turn = UserTurn();
            await new SqliteMemoryStore(factory, TimeProvider.System).SaveAsync(turn.Snapshot, 0);
            var a = turn.Run.ActivationId.ToString("D"); var s = turn.Snapshot.SessionId.ToString("D");
            var agent = Owner.AgentInstanceId.ToString("D"); var profile = Owner.ProfileId.ToString("D");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Activations (ActivationId, SessionId, DedupeKey, AdmissionHash, AgentInstanceId, ProfileId, PayloadJson, AdmittedAtUtc) VALUES ({a}, {s}, 'retained', 'retained', {agent}, {profile}, '{{}}', 0)");
            var error = await Assert.ThrowsAsync<SqliteException>(() => db.Database.MigrateAsync());
            Assert.Contains("Execution_history_requires_explicit_reset", error.Message);
            Assert.Equal(1, await db.Activations.CountAsync());
            Assert.Equal(1, await db.Sessions.CountAsync());
            Assert.Equal("20261016233720_TenantSessionOwnership", (await db.Database.GetAppliedMigrationsAsync()).Last());
        }
        finally
        {
            using var connection = new SqliteConnection($"Data Source={path}");
            SqliteConnection.ClearPool(connection);
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }

}
