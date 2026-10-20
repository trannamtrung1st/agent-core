using AgentCore.Application.Events;
using AgentCore.Domain.Events;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Tests;

public sealed class TenantWebhookMigrationTests
{
    [Fact]
    public async Task Webhook_ownership_rebuild_preserves_existing_source_rows_and_all_incumbent_guards_on_a_disposable_roundtrip()
    {
        var path = Path.Combine(Path.GetTempPath(), "tenant-webhook-migration-" + Guid.NewGuid().ToString("N") + ".db");
        var factory = new Factory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite("Data Source=" + path + ";Foreign Keys=True").Options);
        try
        {
            await using var db = factory.CreateDbContext(); await db.Database.MigrateAsync(); Assert.False(db.Database.HasPendingModelChanges());
            var at = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var source = new WebhookEvent(Guid.NewGuid(), "Retained pre-cutover source", WebhookEventKind.Webhook, "retained.legacy", WebhookTokens.Hash("synthetic-old-key"), WebhookEventStatus.Active, 1, at, at);
            var store = new SqliteExternalEventStore(factory); await store.CreateAsync(source);
            var guards = await Guards(db); Assert.Contains("TR_TenantWebhook_Source_Insert", guards); Assert.Contains("TR_TenantWebhook_Receipt_Insert", guards); Assert.Contains("TR_TenantApprovals_AutomationAdmission_INSERT", guards);
            await db.GetService<IMigrator>().MigrateAsync("20261021010000_TenantAutomationAdmissionApproval"); var prior = await Guards(db);
            Assert.Equal(8, guards.Length - prior.Length); Assert.All(prior, name => Assert.Contains(name, guards));
            // The downgraded schema intentionally lacks the new model columns; inspect its old row directly.
            await db.Database.OpenConnectionAsync(); using (var old = db.Database.GetDbConnection().CreateCommand())
            {
                old.CommandText = "SELECT EventKey, CredentialHash FROM WebhookEvents WHERE ResourceId = $id";
                var parameter = old.CreateParameter(); parameter.ParameterName = "$id"; parameter.Value = source.ResourceId.ToString("D"); old.Parameters.Add(parameter);
                using var reader = await old.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync()); Assert.Equal(source.EventKey, reader.GetString(0)); Assert.Equal(source.CredentialHash, reader.GetString(1));
            }
            await db.Database.MigrateAsync(); Assert.Equal(guards, await Guards(db)); Assert.False(db.Database.HasPendingModelChanges());
            var restored = (await store.GetAsync(source.ResourceId))!; Assert.Null(restored.TenantId); Assert.Null(restored.CreatedByPrincipalId); Assert.Equal(source.DisplayName, restored.DisplayName); Assert.Equal(source.Revision, restored.Revision);
            await db.Database.OpenConnectionAsync(); using var command = db.Database.GetDbConnection().CreateCommand(); command.CommandText = "PRAGMA foreign_key_check"; using var rows = await command.ExecuteReaderAsync(); Assert.False(await rows.ReadAsync());
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(path); File.Delete(path + "-wal"); File.Delete(path + "-shm"); }
    }
    private static async Task<string[]> Guards(AgentCoreDbContext db)
    {
        await db.Database.OpenConnectionAsync(); using var command = db.Database.GetDbConnection().CreateCommand(); command.CommandText = "SELECT name FROM sqlite_master WHERE type='trigger' ORDER BY name";
        using var reader = await command.ExecuteReaderAsync(); var names = new List<string>(); while (await reader.ReadAsync()) names.Add(reader.GetString(0)); return names.ToArray();
    }
    private sealed class Factory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    { public AgentCoreDbContext CreateDbContext() => new(options); }
}
