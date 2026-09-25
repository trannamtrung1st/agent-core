using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;

internal static class MigrationSessionSeed
{
    public const string PrePinnedPersonaRevisionMigrationId = "20260925101918_P7ManagedAgentInstanceRevisionToken";

    public static async Task CopyPersistedSessionAsync(
        IDbContextFactory<AgentCoreDbContext> targetFactory,
        SessionSnapshot snapshot,
        string targetMigrationId,
        CancellationToken cancellationToken = default)
    {
        var referencePath = Path.Combine(Path.GetTempPath(), $"agent-core-seed-ref-{Guid.NewGuid():N}.db");
        var referenceOptions = new DbContextOptionsBuilder<AgentCoreDbContext>()
            .UseSqlite($"Data Source={referencePath}")
            .Options;
        var referenceFactory = new ReferenceContextFactory(referenceOptions);
        try
        {
            await using (var referenceDb = await referenceFactory.CreateDbContextAsync(cancellationToken))
            {
                await referenceDb.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
            }

            var referenceMemory = new SqliteMemoryStore(referenceFactory, TimeProvider.System);
            await referenceMemory.SaveAsync(snapshot, 0, cancellationToken).ConfigureAwait(false);

            await using var targetDb = await targetFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await targetDb.Database.MigrateAsync(targetMigrationId, cancellationToken).ConfigureAwait(false);

            var sessionKey = snapshot.SessionId.ToString("D");
            await CopySessionTablesAsync(referencePath, targetDb, sessionKey, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(referencePath))
            {
                File.Delete(referencePath);
            }
        }
    }

    private static async Task CopySessionTablesAsync(
        string referencePath,
        AgentCoreDbContext targetDb,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var connection = targetDb.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var attach = connection.CreateCommand())
        {
            attach.CommandText = "ATTACH DATABASE @path AS src;";
            var pathParameter = attach.CreateParameter();
            pathParameter.ParameterName = "@path";
            pathParameter.Value = referencePath;
            attach.Parameters.Add(pathParameter);
            await attach.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var table in new[] { "Sessions", "Snapshots", "Entries" })
        {
            var columns = await ListColumnsAsync(connection, "src", table, cancellationToken).ConfigureAwait(false);
            columns = columns
                .Where(column => !string.Equals(column, "PinnedPersonaRevision", StringComparison.Ordinal))
                .ToArray();
            if (columns.Length == 0)
            {
                continue;
            }

            var columnList = string.Join(", ", columns.Select(column => $"\"{column}\""));
            var sql =
                "INSERT OR REPLACE INTO \"" + table + "\" (" + columnList + ") " +
                "SELECT " + columnList + " FROM src.\"" + table + "\" WHERE \"SessionId\" = {0}";
#pragma warning disable EF1002
            await targetDb.Database.ExecuteSqlRawAsync(sql, sessionId).ConfigureAwait(false);
#pragma warning restore EF1002
        }

        await using (var detach = connection.CreateCommand())
        {
            detach.CommandText = "DETACH DATABASE src;";
            await detach.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string[]> ListColumnsAsync(
        System.Data.Common.DbConnection connection,
        string schema,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {schema}.table_info('{table}');";
        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            columns.Add(reader.GetString(1));
        }

        return columns.ToArray();
    }

    private sealed class ReferenceContextFactory(DbContextOptions<AgentCoreDbContext> options)
        : IDbContextFactory<AgentCoreDbContext>
    {
        public AgentCoreDbContext CreateDbContext() => new(options);

        public ValueTask<AgentCoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CreateDbContext());
    }
}
