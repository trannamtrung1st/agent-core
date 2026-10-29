using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    [Fact]
    public async Task DynamicModels_canonical_identity_preserves_all_guard_owners_and_renames_physical_references()
    {
        await using var f = await Fixture.Create(true, targetMigration: "20261025085000_TenantDynamicModels");
        await using var db = f.Contexts!.CreateDbContext();
        await db.GetService<IMigrator>().MigrateAsync("20261025085000_TenantDynamicModels");
        var before = await IdentityGuards(db);
        Assert.Equal(186, before.Count);
        await db.GetService<IMigrator>().MigrateAsync("20261025090000_CanonicalModelIdentity");
        var after = await IdentityGuards(db);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        var changed = new[] { "TR_TenantApprovals_AutomationAdmission_INSERT", "TR_TenantApprovals_ConfigurationRemedy_INSERT",
            "TR_TenantApprovals_CredentialBinding_INSERT", "TR_TenantOccurrences_Source_Update" };
        foreach (var (name, sql) in before)
        {
            if (changed.Contains(name))
            {
                Assert.NotEqual(sql, after[name]);
                Assert.DoesNotContain("CatalogKey", after[name]); Assert.DoesNotContain("ProviderAlias", after[name]);
                Assert.DoesNotContain(".ModelId", after[name]);
            }
            else Assert.Equal(sql, after[name]);
        }
        foreach (var table in new[] { "SessionSnapshots", "ConversationEntries", "TriggerOccurrences" })
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = $"SELECT name FROM pragma_table_info('{table}')";
            await using var rows = await command.ExecuteReaderAsync(); var columns = new List<string>();
            while (await rows.ReadAsync()) columns.Add(rows.GetString(0));
            Assert.Contains("ModelOfferingId", columns); Assert.Contains("ModelConnectionId", columns); Assert.Contains("ExternalModelId", columns);
            Assert.DoesNotContain("ModelCatalogKey", columns); Assert.DoesNotContain("ModelProviderAlias", columns); Assert.DoesNotContain("ModelId", columns);
        }
        await using var integrity = db.Database.GetDbConnection().CreateCommand();
        integrity.CommandText = "SELECT count(*) FROM pragma_foreign_key_check";
        Assert.Equal(0L, await integrity.ExecuteScalarAsync());
        integrity.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='index' AND tbl_name='TenantApprovalRequests' AND sql IS NOT NULL";
        Assert.Equal(13L, await integrity.ExecuteScalarAsync());
    }

    [Fact]
    public async Task DynamicModels_canonical_identity_refuses_downgrade_with_a_retained_pin_without_schema_or_history_loss()
    {
        await using var f = await Fixture.Create(true); var definition = (await f.BuiltIns.ListAsync()).First();
        var snapshot = AgentRunTestFixtures.Snapshot(new(Guid.NewGuid(), Guid.NewGuid()), definition, f.Clock.GetUtcNow());
        await f.Memory.SaveAsync(snapshot, 0);
        await f.SelectHistoricalConversationAsync("20261025090000_CanonicalModelIdentity");
        await using var db = f.Contexts!.CreateDbContext();
        var guards = await IdentityGuards(db);
        var applied = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
        var error = await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync("20261025085000_TenantDynamicModels"));
        Assert.Contains("LegacyModelDataResetRequired", error.Message);
        Assert.Equal("20261025090000_CanonicalModelIdentity", (await db.Database.GetAppliedMigrationsAsync()).Last());
        Assert.Equal(applied, await db.Database.GetAppliedMigrationsAsync());
        Assert.Equal(guards, await IdentityGuards(db));
        // Read the frozen physical columns; today's mapping includes later preferences.
        Assert.Equal(snapshot.ModelSelection!.OfferingId, await db.Database.SqlQueryRaw<string>("SELECT ModelOfferingId AS Value FROM SessionSnapshots").SingleAsync());
        Assert.Equal(snapshot.ModelSelection.ConnectionId, await db.Database.SqlQueryRaw<string>("SELECT ModelConnectionId AS Value FROM SessionSnapshots").SingleAsync());
        Assert.Equal(snapshot.ModelSelection.ExternalModelId, await db.Database.SqlQueryRaw<string>("SELECT ExternalModelId AS Value FROM SessionSnapshots").SingleAsync());
        Assert.Equal(snapshot.ModelSelection.SelectionSource.ToString(), await db.Database.SqlQueryRaw<string>("SELECT ModelSelectionSource AS Value FROM SessionSnapshots").SingleAsync());
        Assert.Equal(snapshot.ModelSelection.ReasoningEffort, await db.Database.SqlQueryRaw<string?>("SELECT ModelReasoningEffort AS Value FROM SessionSnapshots").SingleAsync());
        Assert.Null(await db.Database.SqlQueryRaw<string?>("SELECT ModelBindingJson AS Value FROM SessionSnapshots").SingleAsync());
        Assert.Equal(snapshot.Entries.Single().Text, await db.Database.SqlQueryRaw<string>("SELECT Text AS Value FROM ConversationEntries").SingleAsync());
    }

    [Theory]
    [InlineData("catalogKey", "null")]
    [InlineData("modelKey", "null")]
    [InlineData("catalogKey", "\"retired-alias\"")]
    public async Task DynamicModels_canonical_identity_refuses_retired_document_fields_before_renaming_columns(string property, string value)
    {
        await using var f = await Fixture.Create(true, targetMigration: "20261025085000_TenantDynamicModels"); await using var db = f.Contexts!.CreateDbContext();
        await db.GetService<IMigrator>().MigrateAsync("20261025085000_TenantDynamicModels");
        var document = "{\"nested\":{\"" + property + "\":" + value + "}}";
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO AgentDefinitionDrafts (DraftId,DefinitionId,Revision,CandidateJson,SourceKind,CreatedAtUtc,UpdatedAtUtc) VALUES ({"retired-model-field"},{"retained-definition"},{1L},{document},{0},{0L},{0L})");
        var error = await Assert.ThrowsAsync<SqliteException>(() => db.Database.MigrateAsync());
        Assert.Contains("LegacyModelDataResetRequired", error.Message);
        Assert.Equal("20261025085000_TenantDynamicModels", (await db.Database.GetAppliedMigrationsAsync()).Last());
        await db.Database.OpenConnectionAsync(); await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT CandidateJson FROM AgentDefinitionDrafts WHERE DraftId='retired-model-field'";
        Assert.Equal(document, await command.ExecuteScalarAsync());
        command.CommandText = "SELECT count(*) FROM pragma_table_info('SessionSnapshots') WHERE name='ModelCatalogKey'";
        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }

    private static async Task<Dictionary<string, string>> IdentityGuards(AgentCoreDbContext db)
    {
        await db.Database.OpenConnectionAsync(); await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT name,sql FROM sqlite_master WHERE type='trigger' ORDER BY name";
        await using var rows = await command.ExecuteReaderAsync(); var result = new Dictionary<string, string>();
        while (await rows.ReadAsync()) result.Add(rows.GetString(0), rows.GetString(1).TrimEnd(';'));
        return result;
    }
}
