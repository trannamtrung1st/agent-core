using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit.Abstractions;

namespace AgentCore.Infrastructure.Tests;

public sealed class CanonicalSchemaAcceptanceTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Canonical_startup_and_reopen_preserve_model_columns_foreign_keys_and_all_schema_definitions()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tenant-schema-acceptance-{Guid.NewGuid():N}.db");
        var factory = new ContextFactory(new DbContextOptionsBuilder<AgentCoreDbContext>()
            .UseSqlite($"Data Source={path}")
            .AddInterceptors(new SqlitePragmaInterceptor(5000)).Options);
        try
        {
            await new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync();
            string[] schema;
            await using (var db = factory.CreateDbContext())
            {
                Assert.False(db.Database.HasPendingModelChanges());
                Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync());
                Assert.Empty(await db.Database.GetPendingMigrationsAsync());
                var model = db.GetService<IDesignTimeModel>().Model.GetRelationalModel();
                var tables = await db.Database.SqlQueryRaw<string>(
                    "SELECT name AS Value FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name NOT IN ('__EFMigrationsHistory','__EFMigrationsLock') ORDER BY name").ToArrayAsync();
                Assert.Equal(model.Tables.Select(t => t.Name).Order(), tables);
                Assert.DoesNotContain("OwnerCapabilities", tables);
                Assert.DoesNotContain("AgentWorkspaceItems", tables);

                var columnCount = 0;
                var foreignKeyCount = 0;
                foreach (var table in model.Tables.OrderBy(t => t.Name))
                {
                    var columns = await db.Database.SqlQueryRaw<Column>(
                        "SELECT name AS Name, type AS StoreType, \"notnull\" AS Required FROM pragma_table_info({0}) ORDER BY name", table.Name).ToArrayAsync();
                    Assert.Equal(table.Columns.OrderBy(c => c.Name).Select(c => new Column(c.Name, c.StoreType.ToUpperInvariant(), c.IsNullable ? 0 : 1)), columns);
                    columnCount += columns.Length;

                    var keys = await db.Database.SqlQueryRaw<ForeignKeyColumn>(
                        "SELECT id AS Id, seq AS Sequence, \"table\" AS PrincipalTable, \"from\" AS SourceColumn, \"to\" AS PrincipalColumn FROM pragma_foreign_key_list({0})", table.Name).ToArrayAsync();
                    var expected = table.ForeignKeyConstraints.Select(k => Key(k.PrincipalTable.Name,
                        k.Columns.Select(c => c.Name), k.PrincipalColumns.Select(c => c.Name))).Order().ToArray();
                    var actual = keys.GroupBy(k => k.Id).Select(g => Key(g.First().PrincipalTable,
                        g.OrderBy(k => k.Sequence).Select(k => k.SourceColumn), g.OrderBy(k => k.Sequence).Select(k => k.PrincipalColumn))).Order().ToArray();
                    Assert.Equal(expected, actual);
                    foreignKeyCount += actual.Length;
                }

                Assert.Equal(1, (await db.Database.SqlQueryRaw<int>("PRAGMA foreign_keys").ToArrayAsync()).Single());
                Assert.Equal("wal", (await db.Database.SqlQueryRaw<string>("PRAGMA journal_mode").ToArrayAsync()).Single());
                Assert.Equal(5000, (await db.Database.SqlQueryRaw<int>("PRAGMA busy_timeout").ToArrayAsync()).Single());
                Assert.Empty(await db.Database.SqlQueryRaw<ForeignKeyFailure>("PRAGMA foreign_key_check").ToArrayAsync());
                Assert.Equal("ok", (await db.Database.SqlQueryRaw<string>("PRAGMA integrity_check").ToArrayAsync()).Single());
                schema = await Schema(db);
                output.WriteLine($"Physical/model parity: {tables.Length} tables, {columnCount} columns, {foreignKeyCount} foreign keys; {db.Database.GetMigrations().Count()} migrations applied.");
            }

            await new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync();
            await using var reopened = factory.CreateDbContext();
            Assert.Equal(schema, await Schema(reopened));
            Assert.False(reopened.Database.HasPendingModelChanges());
            Assert.Equal(1, (await reopened.Database.SqlQueryRaw<int>("PRAGMA foreign_keys").ToArrayAsync()).Single());
            Assert.Equal("wal", (await reopened.Database.SqlQueryRaw<string>("PRAGMA journal_mode").ToArrayAsync()).Single());
            Assert.Equal(5000, (await reopened.Database.SqlQueryRaw<int>("PRAGMA busy_timeout").ToArrayAsync()).Single());
            Assert.Empty(await reopened.Database.SqlQueryRaw<ForeignKeyFailure>("PRAGMA foreign_key_check").ToArrayAsync());
            output.WriteLine("Repeated canonical startup/reopen preserves every table, index and trigger definition; foreign keys remain enabled and consistent.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path); File.Delete(path + "-wal"); File.Delete(path + "-shm");
        }
    }

    private static string Key(string table, IEnumerable<string> source, IEnumerable<string> target) =>
        $"{string.Join(',', source)}->{table}({string.Join(',', target)})";

    private static Task<string[]> Schema(AgentCoreDbContext db) => db.Database.SqlQueryRaw<string>(
        "SELECT type || ':' || name || ':' || coalesce(sql,'') AS Value FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY type,name").ToArrayAsync();

    private sealed record Column(string Name, string StoreType, int Required);
    private sealed record ForeignKeyColumn(int Id, int Sequence, string PrincipalTable, string SourceColumn, string PrincipalColumn);
    private sealed record ForeignKeyFailure(string Table, long? RowId, string Parent, int FkId);
    private sealed class ContextFactory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    {
        public AgentCoreDbContext CreateDbContext() => new(options);
    }
}
