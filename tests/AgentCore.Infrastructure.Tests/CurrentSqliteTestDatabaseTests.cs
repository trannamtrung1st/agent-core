using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;

public sealed class CurrentSqliteTestDatabaseTests
{
    [Fact]
    public async Task Current_schema_copies_preserve_full_schema_history_and_isolate_writes()
    {
        var root = Path.Combine(Path.GetTempPath(), "agent-core-schema-copy-proof-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var copies = Enumerable.Range(0, 8).Select(i => Path.Combine(root, $"copy-{i}.db")).ToArray();
            await Task.WhenAll(copies.Select(CurrentSqliteTestDatabase.SeedAsync));
            var first = copies[0]; var second = copies[1];
            await using var a = Open(first); await using var b = Open(second); await using var fresh = Open(Path.Combine(root, "fresh.db"));
            await fresh.Database.MigrateAsync();
            Assert.Equal(await Schema(fresh), await Schema(a)); Assert.Equal(await Schema(fresh), await Schema(b));
            Assert.Equal(await fresh.Database.GetAppliedMigrationsAsync(), await a.Database.GetAppliedMigrationsAsync());
            Assert.False(a.Database.HasPendingModelChanges()); Assert.False(b.Database.HasPendingModelChanges());
            Assert.Empty(await a.Database.SqlQueryRaw<string>("SELECT \"table\" AS Value FROM pragma_foreign_key_check").ToArrayAsync());
            await a.Database.ExecuteSqlRawAsync("CREATE TABLE FixtureIsolationMarker (Value INTEGER NOT NULL); INSERT INTO FixtureIsolationMarker VALUES (7)");
            Assert.DoesNotContain(await Schema(b), sql => sql.Contains("FixtureIsolationMarker", StringComparison.Ordinal));
            var third = Path.Combine(root, "third.db"); await CurrentSqliteTestDatabase.SeedAsync(third);
            await using var c = Open(third); Assert.Equal(await Schema(fresh), await Schema(c));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Existing_destination_is_refused_without_changing_its_bytes()
    {
        var path = Path.Combine(Path.GetTempPath(), "agent-core-existing-db-proof-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllTextAsync(path, "existing-owned-sentinel");
            await Assert.ThrowsAsync<IOException>(() => CurrentSqliteTestDatabase.SeedAsync(path));
            Assert.Equal("existing-owned-sentinel", await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); }
    }

    private static AgentCoreDbContext Open(string path) => new(new DbContextOptionsBuilder<AgentCoreDbContext>()
        .UseSqlite(new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true, Pooling = false }.ToString()).Options);
    private static Task<string[]> Schema(AgentCoreDbContext db) => db.Database.SqlQueryRaw<string>(
        "SELECT sql AS Value FROM sqlite_master WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%' ORDER BY type,name").ToArrayAsync();
}
