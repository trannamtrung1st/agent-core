using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    internal sealed partial class Fixture
    {
        // Seed only an unowned conversation into a fresh real predecessor. This
        // does not downgrade the source, copy migration history, or bypass guards.
        internal async Task SelectHistoricalConversationAsync(string predecessor)
        {
            Assert.NotNull(Contexts);
            await using (var source = Contexts.CreateDbContext())
            {
                Assert.Equal(0, await source.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM Tenants").SingleAsync());
                Assert.Equal(0, await source.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM AgentRuns").SingleAsync());
                Assert.Equal(0, await source.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM SessionSnapshots WHERE ModelReasoningEffort IS NOT NULL").SingleAsync());
            }
            var destinationPath = Path.Combine(Root, "historical-conversation.db");
            using (new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write)) { }
            var destination = new ContextFactory(new DbContextOptionsBuilder<AgentCoreDbContext>()
                .UseSqlite($"Data Source={destinationPath};Foreign Keys=True;Default Timeout=10").Options);
            await using (var db = destination.CreateDbContext())
            {
                await db.GetService<IMigrator>().MigrateAsync(predecessor);
                await db.Database.OpenConnectionAsync();
                var connection = (SqliteConnection)db.Database.GetDbConnection();
                await using (var attach = connection.CreateCommand())
                {
                    attach.CommandText = "ATTACH DATABASE $path AS fixtureSource";
                    attach.Parameters.AddWithValue("$path", Path.Combine(Root, "store.db"));
                    await attach.ExecuteNonQueryAsync();
                }
                async Task<HashSet<string>> Columns(string schema, string table)
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = $"PRAGMA {schema}.table_info(\"{table}\")";
                    var result = new HashSet<string>(StringComparer.Ordinal);
                    await using var rows = await command.ExecuteReaderAsync();
                    while (await rows.ReadAsync()) result.Add(rows.GetString(1));
                    return result;
                }
                foreach (var table in new[] { "AgentInstances", "Sessions", "SessionSnapshots", "ConversationEntries" })
                {
                    var columns = await Columns("main", table);
                    columns.IntersectWith(await Columns("fixtureSource", table));
                    Assert.NotEmpty(columns);
                    var names = string.Join(",", columns.Order(StringComparer.Ordinal).Select(name => "\"" + name + "\""));
                    var selected = names;
                    if (table == "SessionSnapshots")
                    {
                        names += ",\"ModelHasExplicitReasoningEffort\"";
                        selected += ",0"; // These fixtures declare no explicit reasoning effort.
                    }
                    await using var insert = connection.CreateCommand();
                    insert.CommandText = $"INSERT INTO \"{table}\" ({names}) SELECT {selected} FROM fixtureSource.\"{table}\"";
                    await insert.ExecuteNonQueryAsync();
                    await using var parity = connection.CreateCommand();
                    parity.CommandText = $"SELECT (SELECT count(*) FROM main.\"{table}\") = (SELECT count(*) FROM fixtureSource.\"{table}\")";
                    Assert.Equal(1L, await parity.ExecuteScalarAsync());
                }
                Assert.Equal(1, (await db.Database.SqlQueryRaw<int>("PRAGMA foreign_keys").ToArrayAsync()).Single());
                Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM pragma_foreign_key_check").SingleAsync());
            }
            Contexts = destination;
            Memory = new SqliteMemoryStore(destination, Clock);
        }
    }
}
