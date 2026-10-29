using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantDefinitionIntegrationTests
{
    [Fact]
    public async Task Space_migrations_preserve_predecessor_guards_and_roundtrip_empty_disposable_storage()
    {
        await using var f = await Fixture.Create(true, targetMigration: "20261025080000_SpaceResourceApprovals");
        await using var db = f.Contexts!.CreateDbContext();
        var migrator = db.GetService<IMigrator>();
        static bool NewGuard(string name) => name.StartsWith("TR_SpaceResources_", StringComparison.Ordinal) || name.StartsWith("TR_SpaceBinding_Source_", StringComparison.Ordinal) || name.StartsWith("TR_SpaceSources_", StringComparison.Ordinal) || name.StartsWith("TR_TenantRuns_Context_", StringComparison.Ordinal) || name.StartsWith("TR_SpaceAutomationPins_", StringComparison.Ordinal) || name.StartsWith("TR_Spaces_", StringComparison.Ordinal)
            || name.StartsWith("TR_AgentRuns_Space_", StringComparison.Ordinal) || name.StartsWith("TR_TenantApprovals_Space_", StringComparison.Ordinal)
            || name.StartsWith("TR_SpaceMembership_", StringComparison.Ordinal) || name.StartsWith("TR_SpaceBindings_", StringComparison.Ordinal)
            || name.StartsWith("TR_Sessions_Context", StringComparison.Ordinal) || name.StartsWith("TR_Sessions_Space", StringComparison.Ordinal) || name.StartsWith("TR_TenantSessions_Space", StringComparison.Ordinal)
            || name.StartsWith("TR_Environments_", StringComparison.Ordinal) || name.StartsWith("TR_Variables_", StringComparison.Ordinal) || name.StartsWith("TR_Secrets_", StringComparison.Ordinal);
        async Task<Dictionary<string, string>> Guards()
        {
            await db.Database.OpenConnectionAsync(); await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT name, sql FROM sqlite_master WHERE type='trigger' ORDER BY name";
            var result = new Dictionary<string, string>(); await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) if (!NewGuard(reader.GetString(0))) result.Add(reader.GetString(0), reader.GetString(1));
            return result;
        }
        await migrator.MigrateAsync("20261025080000_SpaceResourceApprovals");
        var before = await Guards(); Assert.True(before.Count > 100);
        await migrator.MigrateAsync("20261023190000_TenantPublicHttp");
        var prior = await Guards();
        var changedCredentials = new[] { "TR_TenantApprovals_AccountAction_Insert", "TR_TenantApprovals_AccountRead_Insert" };
        var changedEvents = new[] { "TR_TenantCore_Fact_Insert", "TR_TenantWebhook_Receipt_Insert", "TR_TenantCore_Delivery_Insert", "TR_TenantCore_Delivery_Update", "TR_TenantWebhook_Delivery_Insert", "TR_TenantWebhook_Delivery_Update", "TR_TenantEvent_Bucket_Insert", "TR_TenantEvent_Bucket_Update" };
        var changed = changedCredentials.Concat(changedEvents).ToArray();
        Assert.Equal(before.Where(p => !changed.Contains(p.Key)), prior.Where(p => !changed.Contains(p.Key)));
        foreach (var name in changedCredentials)
        {
            Assert.Contains("CurrentSpaceCredentialBindings", before[name]);
            Assert.Contains("AgentCredentialBindings", prior[name]);
        }
        foreach (var name in changedEvents) { Assert.Contains("PinnedSpaceId", before[name]); Assert.DoesNotContain("PinnedSpaceId", prior[name]); }
        await migrator.MigrateAsync("20261025080000_SpaceResourceApprovals");
        Assert.Equal(before, await Guards()); Assert.False(db.Database.HasPendingModelChanges());
        await using var check = db.Database.GetDbConnection().CreateCommand(); check.CommandText = "PRAGMA foreign_key_check";
        await using var foreignKeys = await check.ExecuteReaderAsync(); Assert.False(await foreignKeys.ReadAsync());
    }
    [Fact]
    public async Task Workspace_cutover_refuses_retained_unowned_Instance_rows_without_resetting_them()
    {
        await using var f = await Fixture.Create(true, targetMigration: "20261025030000_ContextualExecutionSources");
        await using var db = f.Contexts!.CreateDbContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261025030000_ContextualExecutionSources");
        var instance = Guid.NewGuid(); var at = DateTimeOffset.UtcNow;
        // Seed the predecessor schema directly: the current store includes later model columns.
        var persona = System.Text.Json.JsonSerializer.Serialize(new AgentCore.Domain.Definitions.AgentIdentity("Retained", "Role", "Description", "Tone"));
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO AgentInstances (InstanceId,DefinitionId,ActiveVersion,PersonaJson,Lifecycle,CreatedAtUtc,UpdatedAtUtc,Revision,PersonaRevision) VALUES ({instance.ToString("D")},{"examiner"},{1},{persona},{"Active"},{at.ToUnixTimeMilliseconds()},{at.ToUnixTimeMilliseconds()},{1},{1})");
        var rejected = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => migrator.MigrateAsync());
        Assert.Equal(275, rejected.SqliteExtendedErrorCode); Assert.Contains("ExistingRows=0", rejected.Message);
        Assert.True(await f.Instances.HasBackingInstanceAsync(instance));
        Assert.Empty(await db.Set<AgentCore.Infrastructure.Security.SpaceRecord>().ToArrayAsync());
    }

}
