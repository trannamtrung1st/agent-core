using System.Text.Json;
using AgentCore.Domain.Events;
using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Tests;

public sealed class TenantEventMigrationTests
{
    [Fact]
    public async Task Tenant_event_rebuild_preserves_legacy_facts_and_all_incumbent_guards_on_a_disposable_roundtrip()
    {
        var path = Path.Combine(Path.GetTempPath(), "tenant-event-migration-" + Guid.NewGuid().ToString("N") + ".db");
        var factory = new Factory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite("Data Source=" + path + ";Foreign Keys=True").Options);
        try
        {
            await using var db = factory.CreateDbContext(); await db.GetService<IMigrator>().MigrateAsync("20261021020000_TenantWebhookOwnership");
            var previous = await Guards(db); Assert.Equal(54, previous.Length);
            var at = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var instance = Guid.NewGuid(); var profile = Guid.NewGuid(); var id = Guid.NewGuid();
            var fact = new CoreEventOccurrence(id, "legacy-retained", new TriggerOwner(instance, profile), "run.completed", at, "{}");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO CoreEvents (EventId,DedupeKey,AgentInstanceId,ProfileId,ReceivedAtUtc,PayloadJson,Snapshotted) VALUES ({id.ToString("D")},{fact.DedupeKey},{instance.ToString("D")},{profile.ToString("D")},{at.ToUnixTimeMilliseconds()},{JsonSerializer.Serialize(fact)},0)");
            await db.GetService<IMigrator>().MigrateAsync("20261021030000_TenantEventOwnership");
            // This frozen upgrade declares 68 guards. Later migrations may add
            // guards, but neither upgrade nor a roundtrip may drop these owners.
            var converted = await Guards(db); Assert.Equal(68, converted.Length); Assert.All(previous, name => Assert.Contains(name, converted));
            await db.Database.MigrateAsync(); Assert.False(db.Database.HasPendingModelChanges()); var latest = await Guards(db);
            Assert.All(converted, name => Assert.Contains(name, latest));
            Assert.Contains("TR_TenantCore_Fact_Insert", latest); Assert.Contains("TR_TenantEvent_Occurrence_Insert", latest);
            Assert.Equal(fact.EventId.ToString("D"), (await db.CoreEvents.AsNoTracking().SingleAsync()).EventId);
            var tagged = JsonSerializer.Serialize(fact with { EventId = Guid.NewGuid(), DedupeKey = "legacy-concealed", Owner = TriggerOwner.ForTenant(Guid.NewGuid(), instance, Guid.NewGuid()) });
            foreach (var concealed in new[] { tagged, tagged.Replace("\"Owner\":", "\"owner\":", StringComparison.Ordinal) })
                await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO CoreEvents (EventId,DedupeKey,AgentInstanceId,ProfileId,ReceivedAtUtc,PayloadJson,Snapshotted) VALUES ({Guid.NewGuid().ToString("D")},{"legacy-concealed"},{instance.ToString("D")},{profile.ToString("D")},{at.ToUnixTimeMilliseconds()},{concealed},0)"));
            await db.GetService<IMigrator>().MigrateAsync("20261021020000_TenantWebhookOwnership"); Assert.Equal(previous, await Guards(db));
            await db.Database.MigrateAsync(); Assert.Equal(latest, await Guards(db)); Assert.False(db.Database.HasPendingModelChanges());
            await db.Database.OpenConnectionAsync(); using var command = db.Database.GetDbConnection().CreateCommand(); command.CommandText = "PRAGMA foreign_key_check"; using var rows = await command.ExecuteReaderAsync(); Assert.False(await rows.ReadAsync());
        }
        finally { SqliteConnection.ClearAllPools(); foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix); }
    }
    private static async Task<string[]> Guards(AgentCoreDbContext db)
    {
        await db.Database.OpenConnectionAsync(); using var command = db.Database.GetDbConnection().CreateCommand(); command.CommandText = "SELECT name FROM sqlite_master WHERE type='trigger' ORDER BY name";
        using var reader = await command.ExecuteReaderAsync(); var names = new List<string>(); while (await reader.ReadAsync()) names.Add(reader.GetString(0)); return names.ToArray();
    }
    private sealed class Factory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    { public AgentCoreDbContext CreateDbContext() => new(options); }
}
