using System.Security.Cryptography;
using System.Text;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Tests;

public sealed class OwnerCredentialRetirementMigrationTests
{
    [Fact]
    public async Task Retirement_removes_only_the_obsolete_table_and_never_restores_dropped_credentials_on_rollback()
    {
        var path = Path.Combine(Path.GetTempPath(), "owner-credential-retirement-" + Guid.NewGuid().ToString("N") + ".db");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite("Data Source=" + path + ";Foreign Keys=True").Options;
        try
        {
            await using var db = new AgentCoreDbContext(options);
            var migrations = db.GetService<IMigrator>();
            await migrations.MigrateAsync("20261021060000_TenantAutomationConversation");
            var before = await Tables(db); Assert.Contains("OwnerCapabilities", before);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(new string('9', 64)))).ToLowerInvariant();
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO OwnerCapabilities (TokenHash, CreatedAtUtc) VALUES ({hash}, {123456L})");
            await db.Database.ExecuteSqlRawAsync("INSERT INTO UserProfiles (ProfileId, PreferencesJson, Revision, UpdatedAtUtc) VALUES ('retained-fixture', '{{}}', 7, 123456)");
            await db.Database.MigrateAsync();
            Assert.Equal(before.Where(n => n != "OwnerCapabilities"), await Tables(db));
            Assert.DoesNotContain(db.Model.GetEntityTypes(), type => type.GetTableName() == "OwnerCapabilities");
            Assert.False(db.Database.HasPendingModelChanges());
            var retained = await db.Profiles.AsNoTracking().SingleAsync();
            Assert.Equal(7, retained.Revision); Assert.Equal("{}", retained.PreferencesJson); Assert.Equal(123456L, retained.UpdatedAtUtc);
            await migrations.MigrateAsync("20261021060000_TenantAutomationConversation");
            await db.Database.OpenConnectionAsync(); using (var count = db.Database.GetDbConnection().CreateCommand())
            { count.CommandText = "SELECT count(*) FROM OwnerCapabilities"; Assert.Equal(0L, Convert.ToInt64(await count.ExecuteScalarAsync())); }
            await db.Database.MigrateAsync(); Assert.DoesNotContain("OwnerCapabilities", await Tables(db));
            Assert.False(db.Database.HasPendingModelChanges());
            using var check = db.Database.GetDbConnection().CreateCommand(); check.CommandText = "PRAGMA foreign_key_check";
            using var rows = await check.ExecuteReaderAsync(); Assert.False(await rows.ReadAsync());
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(path); File.Delete(path + "-wal"); File.Delete(path + "-shm"); }
    }
    private static async Task<string[]> Tables(AgentCoreDbContext db)
    {
        await db.Database.OpenConnectionAsync(); using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
        using var rows = await command.ExecuteReaderAsync(); var values = new List<string>();
        while (await rows.ReadAsync()) values.Add(rows.GetString(0)); return values.ToArray();
    }
}
