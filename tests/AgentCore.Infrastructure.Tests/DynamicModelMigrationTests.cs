using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    [Fact]
    public async Task DynamicModels_legacy_pin_refusal_preserves_history_and_predecessor_schema()
    {
        await using var f = await Fixture.Create(true);
        const string predecessor = "20261025080000_SpaceResourceApprovals";
        var definition = (await f.BuiltIns.ListAsync()).First();
        var snapshot = AgentRunTestFixtures.Snapshot(new(Guid.NewGuid(), Guid.NewGuid()), definition, f.Clock.GetUtcNow());
        snapshot = snapshot with { ModelSelection = null, SummaryModel = null,
            Entries = snapshot.Entries.Select(entry => entry with { ModelProvenance = null }).ToArray() };
        await f.Memory.SaveAsync(snapshot, 0);
        await using (var db = f.Contexts!.CreateDbContext()) await db.GetService<IMigrator>().MigrateAsync(predecessor);
        await using (var db = f.Contexts!.CreateDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE SessionSnapshots SET ModelCatalogKey='legacy-model', ModelProviderAlias='primary-llm', ModelId='legacy-model' WHERE SessionId={snapshot.SessionId.ToString("D")}");
            var error = await Assert.ThrowsAsync<SqliteException>(async () => await db.Database.MigrateAsync());
            Assert.Contains("LegacyModelDataResetRequired", error.Message);
            Assert.Equal(predecessor, (await db.Database.GetAppliedMigrationsAsync()).Last());
            await db.Database.OpenConnectionAsync(); await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name IN ('ModelPresets','ProviderConnections','ModelOfferings','TenantModelSettings')";
            Assert.Equal(0L, await command.ExecuteScalarAsync());
            command.CommandText = "SELECT Text FROM ConversationEntries WHERE SessionId=$session";
            var parameter = command.CreateParameter(); parameter.ParameterName = "$session"; parameter.Value = snapshot.SessionId.ToString("D"); command.Parameters.Add(parameter);
            Assert.Equal(snapshot.Entries.Single().Text, await command.ExecuteScalarAsync());
            command.CommandText = "SELECT ModelCatalogKey FROM SessionSnapshots WHERE SessionId=$session";
            Assert.Equal("legacy-model", await command.ExecuteScalarAsync());
        }
        // Startup follows the same explicit-reset contract and never destroys the installed graph.
        await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(async () => await ((SqliteMemoryStore)f.Memory).EnsureCreatedAsync());
    }
}
