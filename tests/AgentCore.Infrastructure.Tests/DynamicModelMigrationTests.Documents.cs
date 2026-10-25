using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    [Theory]
    [InlineData("languageModel", "\"retired-route\"")]
    [InlineData("catalogKey", "\"retired-model\"")]
    [InlineData("providerLanguageModel", "{\"value\":\"retired-route\"}")]
    public async Task DynamicModels_legacy_definition_and_override_documents_refuse_upgrade_before_schema_mutation(string property, string value)
    {
        await using var f = await Fixture.Create(true);
        const string predecessor = "20261023190000_TenantPublicHttp";
        await using (var db = f.Contexts!.CreateDbContext())
        {
            await db.GetService<IMigrator>().MigrateAsync(predecessor);
            // A predecessor draft with a reference that cannot be rebound to a tenant connection.
            var document = "{\"nested\":{\"" + property + "\":" + value + "}}";
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO AgentDefinitionDrafts (DraftId,DefinitionId,Revision,CandidateJson,SourceKind,CreatedAtUtc,UpdatedAtUtc) VALUES ({"legacy-draft"},{"legacy-definition"},{1L},{document},{0},{0L},{0L})");
            var error = await Assert.ThrowsAsync<SqliteException>(() => db.Database.MigrateAsync());
            Assert.Contains("LegacyModelDataResetRequired", error.Message);
            Assert.Equal(predecessor, (await db.Database.GetAppliedMigrationsAsync()).Last());
            await db.Database.OpenConnectionAsync(); await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT CandidateJson FROM AgentDefinitionDrafts WHERE DraftId='legacy-draft'";
            Assert.Equal(document, await command.ExecuteScalarAsync());
            command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='ModelPresets'";
            Assert.Equal(0L, await command.ExecuteScalarAsync());
        }
    }

    [Fact]
    public async Task DynamicModels_empty_tracked_predecessor_upgrades_on_startup_and_preserves_unrelated_data()
    {
        await using var f = await Fixture.Create(true);
        await using (var db = f.Contexts!.CreateDbContext())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261023190000_TenantPublicHttp");
            await db.Database.ExecuteSqlRawAsync("INSERT INTO UserProfiles (ProfileId,PreferencesJson,Revision,UpdatedAtUtc) VALUES ('retained-independent-profile','{{}}',9,123)");
        }
        await ((SqliteMemoryStore)f.Memory).EnsureCreatedAsync();
        await ((SqliteMemoryStore)f.Memory).EnsureCreatedAsync();
        await using var reopened = f.Contexts!.CreateDbContext();
        Assert.Equal(7, await reopened.Set<ModelPresetRecord>().CountAsync());
        Assert.Equal(9, (await reopened.Profiles.SingleAsync(p => p.ProfileId == "retained-independent-profile")).Revision);
        Assert.Equal(reopened.Database.GetMigrations(), await reopened.Database.GetAppliedMigrationsAsync());
    }
}
