using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Tests;

public sealed class TenantPublicDownloadMigrationTests
{
    [Fact]
    public async Task Public_download_receipt_column_roundtrips_without_changing_existing_guards_and_matches_current_model()
    {
        var path = Path.Combine(Path.GetTempPath(), "download-migration-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite("Data Source=" + path + ";Foreign Keys=True").Options;
            await using var db = new AgentCoreDbContext(options); var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20261023160000_RetireEmbeddedRunApproval");
            var before = await Guards(db);
            await migrator.MigrateAsync(); AssertExtended(before, await Guards(db)); Assert.False(db.Database.HasPendingModelChanges());
            await migrator.MigrateAsync("20261023160000_RetireEmbeddedRunApproval"); Assert.Equal(before, await Guards(db));
            await migrator.MigrateAsync(); AssertExtended(before, await Guards(db)); Assert.False(db.Database.HasPendingModelChanges());
            await db.Database.OpenConnectionAsync(); using var check = db.Database.GetDbConnection().CreateCommand();
            check.CommandText = "PRAGMA foreign_key_check"; using var rows = await check.ExecuteReaderAsync(); Assert.False(await rows.ReadAsync());
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(path); File.Delete(path + "-wal"); File.Delete(path + "-shm"); }
    }
    private static void AssertExtended(SortedDictionary<string,string> before, SortedDictionary<string,string> after)
    {
        Assert.Equal(before.Keys, after.Keys);
        foreach (var item in before)
        {
            var expected = item.Key is "trigger:TR_AgentRuns_DirectWorkspace_Insert" or "trigger:TR_AgentRuns_DirectWorkspace_Update"
                ? item.Value.Replace("'office.spreadsheet.edit')", "'office.spreadsheet.edit','web.download')", StringComparison.Ordinal) : item.Value;
            Assert.Equal(expected, after[item.Key]);
        }
    }
    private static async Task<SortedDictionary<string,string>> Guards(AgentCoreDbContext db)
    {
        await db.Database.OpenConnectionAsync(); using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT type || ':' || name,sql FROM sqlite_master WHERE type IN ('trigger','index') AND sql IS NOT NULL ORDER BY type,name";
        using var rows = await command.ExecuteReaderAsync(); var result = new SortedDictionary<string,string>(); while (await rows.ReadAsync()) result.Add(rows.GetString(0), rows.GetString(1)); return result;
    }
}
