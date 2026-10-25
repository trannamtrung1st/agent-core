using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Tests;

public sealed class SpaceAutomationPinMigrationTests
{
    [Fact]
    public async Task Automation_pin_migration_preserves_every_predecessor_guard_and_existing_unpinned_source_on_roundtrip()
    {
        var path = Path.Combine(Path.GetTempPath(), "space-pin-roundtrip-" + Guid.NewGuid().ToString("N") + ".db");
        var factory = new Factory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite("Data Source=" + path + ";Foreign Keys=True").Options);
        try
        {
            await using var db = factory.CreateDbContext(); var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20261025040000_SpaceWorkspaceOwnership");
            async Task<Dictionary<string, string>> Guards()
            {
                await db.Database.OpenConnectionAsync(); await using var query = db.Database.GetDbConnection().CreateCommand();
                query.CommandText = "SELECT name,sql FROM sqlite_master WHERE type='trigger' ORDER BY name";
                var guards = new Dictionary<string, string>(); await using var reader = await query.ExecuteReaderAsync();
                while (await reader.ReadAsync()) if (!reader.GetString(0).StartsWith("TR_SpaceAutomationPins_", StringComparison.Ordinal) && !reader.GetString(0).StartsWith("TR_TenantRuns_Context_", StringComparison.Ordinal) && !reader.GetString(0).StartsWith("TR_SpaceResources_", StringComparison.Ordinal) && !reader.GetString(0).StartsWith("TR_SpaceBinding_Source_", StringComparison.Ordinal) && !reader.GetString(0).StartsWith("TR_SpaceSources_", StringComparison.Ordinal)) guards.Add(reader.GetString(0), reader.GetString(1));
                return guards;
            }
            var changedEvents = new HashSet<string>(StringComparer.Ordinal) { "TR_TenantCore_Fact_Insert", "TR_TenantWebhook_Receipt_Insert", "TR_TenantCore_Delivery_Insert", "TR_TenantCore_Delivery_Update", "TR_TenantWebhook_Delivery_Insert", "TR_TenantWebhook_Delivery_Update", "TR_TenantEvent_Bucket_Insert", "TR_TenantEvent_Bucket_Update" };
            void SameGuards(Dictionary<string, string> expected, Dictionary<string, string> actual, bool extended)
            {
                Assert.Equal(expected.Keys, actual.Keys);
                foreach (var item in expected)
                    if (extended && changedEvents.Contains(item.Key))
                    { Assert.Contains("PinnedSpaceId", actual[item.Key]); Assert.Contains("'ExpressionVersion','FilterHash'", actual[item.Key]); }
                    else Assert.Equal(item.Value, actual[item.Key]);
            }
            var previous = await Guards(); Assert.True(previous.Count > 100);
            await migrator.MigrateAsync(); SameGuards(previous, await Guards(), true); Assert.False(db.Database.HasPendingModelChanges());
            var at = DateTimeOffset.UtcNow; var timing = new OneShotSchedule(at.AddHours(1), "UTC");
            var legacy = new Automation(Guid.NewGuid(), new(Guid.NewGuid(), Guid.NewGuid()), AutomationStatus.Active, "Retain unpinned source",
                new ScheduleTrigger(timing), timing.AtUtc, null, 0, 1, 1, new(TriggerAuthorizationOrigin.AdminOwner, null, null, at, at), null);
            var store = new SqliteTriggerStore(factory); await store.CreateAsync(legacy);
            await migrator.MigrateAsync("20261025040000_SpaceWorkspaceOwnership"); SameGuards(previous, await Guards(), false);
            await migrator.MigrateAsync(); SameGuards(previous, await Guards(), true);
            var retained = (await store.GetAsync(legacy.Owner, legacy.AutomationId))!;
            Assert.Equal(legacy.Instructions, retained.Instructions); Assert.Equal(legacy.Owner, retained.Owner);
            Assert.Null(retained.PinnedSpaceId); Assert.Null(retained.PinnedEnvironmentId);
            await using var check = db.Database.GetDbConnection().CreateCommand(); check.CommandText = "PRAGMA foreign_key_check";
            await using var violations = await check.ExecuteReaderAsync(); Assert.False(await violations.ReadAsync());
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(path); File.Delete(path + "-wal"); File.Delete(path + "-shm"); }
    }
    private sealed class Factory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    { public AgentCoreDbContext CreateDbContext() => new(options); }
}
