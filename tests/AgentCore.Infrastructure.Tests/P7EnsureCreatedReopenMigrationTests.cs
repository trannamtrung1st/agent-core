using AgentCore.Application.Sessions;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;

public sealed class P7EnsureCreatedReopenMigrationTests
{
    [Fact]
    public async Task Reopen_migrates_missing_agent_definition_publications_table()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-p7-partial-lifecycle-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        var factory = new TestFactory(options);
        try
        {
            await new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync();
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS \"AgentDefinitionPublications\";");
                await db.Database.ExecuteSqlRawAsync(
                    """
                    DELETE FROM "__EFMigrationsHistory"
                    WHERE "MigrationId" IN (
                        '20260925071140_P7DefinitionLifecycle',
                        '20260925084907_P7DefinitionResources',
                        '20260925101355_P7ManagedAgentInstance',
                        '20260925101918_P7ManagedAgentInstanceRevisionToken',
                        '20260925103832_P7PinnedPersonaRevision',
                        '20260925150254_P7DefinitionDraftEvaluation',
                        '20260925161000_P7AdminEvents');
                    """);
            }

            await new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync();

            await using var verify = await factory.CreateDbContextAsync();
            var tables = await TableNamesAsync(verify);
            Assert.Contains("AgentDefinitionPublications", tables);
            Assert.True(await MigrationAppliedAsync(verify, "20260925071140_P7DefinitionLifecycle"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task Reopen_repairs_malformed_agent_definition_publications_columns()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-p7-malformed-publications-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        var factory = new TestFactory(options);
        try
        {
            await new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync();
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS \"AgentDefinitionPublications\";");
                await db.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE "AgentDefinitionPublications" (
                        "DefinitionId" TEXT NOT NULL,
                        "Version" INTEGER NOT NULL,
                        "PayloadJson" TEXT NOT NULL,
                        "SourceDraftRevision" INTEGER NOT NULL,
                        "Status" INTEGER NOT NULL,
                        CONSTRAINT "PK_AgentDefinitionPublications" PRIMARY KEY ("DefinitionId", "Version")
                    );
                    """);
                await db.Database.ExecuteSqlRawAsync(
                    """
                    DELETE FROM "__EFMigrationsHistory"
                    WHERE "MigrationId" IN (
                        '20260925071140_P7DefinitionLifecycle',
                        '20260925084907_P7DefinitionResources',
                        '20260925101355_P7ManagedAgentInstance',
                        '20260925101918_P7ManagedAgentInstanceRevisionToken',
                        '20260925103832_P7PinnedPersonaRevision',
                        '20260925150254_P7DefinitionDraftEvaluation',
                        '20260925161000_P7AdminEvents');
                    """);
            }

            await new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync();

            await using var verify = await factory.CreateDbContextAsync();
            Assert.True(await ColumnExistsAsync(verify, "AgentDefinitionPublications", "MetadataRevision"));
            Assert.True(await ColumnExistsAsync(verify, "AgentDefinitionPublications", "PublishedAtUtc"));
            Assert.True(await MigrationAppliedAsync(verify, "20260925071140_P7DefinitionLifecycle"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task Reopen_rejects_non_empty_malformed_agent_definition_publications_columns()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-p7-nonempty-malformed-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        var factory = new TestFactory(options);
        try
        {
            await new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync();
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS \"AgentDefinitionPublications\";");
                await db.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE "AgentDefinitionPublications" (
                        "DefinitionId" TEXT NOT NULL,
                        "Version" INTEGER NOT NULL,
                        "PayloadJson" TEXT NOT NULL,
                        "SourceDraftRevision" INTEGER NOT NULL,
                        "Status" INTEGER NOT NULL,
                        CONSTRAINT "PK_AgentDefinitionPublications" PRIMARY KEY ("DefinitionId", "Version")
                    );
                    """);
                await db.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO "AgentDefinitionPublications"
                    ("DefinitionId", "Version", "PayloadJson", "SourceDraftRevision", "Status")
                    VALUES ('examiner', 1, '{{}}', 1, 0);
                    """);
                await db.Database.ExecuteSqlRawAsync(
                    """
                    DELETE FROM "__EFMigrationsHistory"
                    WHERE "MigrationId" IN (
                        '20260925071140_P7DefinitionLifecycle',
                        '20260925084907_P7DefinitionResources',
                        '20260925101355_P7ManagedAgentInstance',
                        '20260925101918_P7ManagedAgentInstanceRevisionToken',
                        '20260925103832_P7PinnedPersonaRevision',
                        '20260925150254_P7DefinitionDraftEvaluation',
                        '20260925161000_P7AdminEvents');
                    """);
            }

            var error = await Assert.ThrowsAsync<AgentCoreException>(() =>
                new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync().AsTask());
            Assert.Contains("non-empty incompatible schema", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task Reopen_does_not_stamp_pinned_persona_when_nullability_is_incompatible()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-p7-pinned-nullability-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        var factory = new TestFactory(options);
        try
        {
            await new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync();
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.ExecuteSqlRawAsync(
                    """
                    ALTER TABLE "Sessions" DROP COLUMN "PinnedPersonaRevision";
                    ALTER TABLE "Sessions" ADD COLUMN "PinnedPersonaRevision" INTEGER NOT NULL DEFAULT 1;
                    """);
                await db.Database.ExecuteSqlRawAsync(
                    """
                    DELETE FROM "__EFMigrationsHistory"
                    WHERE "MigrationId" = '20260925103832_P7PinnedPersonaRevision';
                    """);
            }

            await Assert.ThrowsAnyAsync<Exception>(() =>
                new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync().AsTask());

            await using var verify = await factory.CreateDbContextAsync();
            Assert.False(await MigrationAppliedAsync(verify, "20260925103832_P7PinnedPersonaRevision"));
            Assert.False(await ColumnIsNullableAsync(verify, "Sessions", "PinnedPersonaRevision"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task Reopen_does_not_stamp_managed_instance_when_persona_revision_nullability_is_incompatible()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-p7-managed-nullability-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        var factory = new TestFactory(options);
        try
        {
            await new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync();
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.ExecuteSqlRawAsync(
                    """
                    ALTER TABLE "AgentInstances" DROP COLUMN "PersonaRevision";
                    ALTER TABLE "AgentInstances" DROP COLUMN "Revision";
                    ALTER TABLE "AgentInstances" ADD COLUMN "PersonaRevision" INTEGER NULL;
                    ALTER TABLE "AgentInstances" ADD COLUMN "Revision" INTEGER NULL;
                    """);
                await db.Database.ExecuteSqlRawAsync(
                    """
                    DELETE FROM "__EFMigrationsHistory"
                    WHERE "MigrationId" IN (
                        '20260925101355_P7ManagedAgentInstance',
                        '20260925101918_P7ManagedAgentInstanceRevisionToken');
                    """);
            }

            await Assert.ThrowsAnyAsync<Exception>(() =>
                new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync().AsTask());

            await using var verify = await factory.CreateDbContextAsync();
            Assert.False(await MigrationAppliedAsync(verify, "20260925101355_P7ManagedAgentInstance"));
            Assert.True(await ColumnIsNullableAsync(verify, "AgentInstances", "PersonaRevision"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task Reopen_migrates_missing_admin_events_operation_id_index()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-p7-partial-admin-events-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        var factory = new TestFactory(options);
        try
        {
            await new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync();
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.ExecuteSqlRawAsync("DROP INDEX IF EXISTS \"IX_AdminEvents_OperationId\";");
                await db.Database.ExecuteSqlRawAsync(
                    """
                    DELETE FROM "__EFMigrationsHistory"
                    WHERE "MigrationId" = '20260925161000_P7AdminEvents';
                    """);
            }

            await new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync();

            await using var verify = await factory.CreateDbContextAsync();
            Assert.True(await IndexExistsAsync(verify, "IX_AdminEvents_OperationId"));
            Assert.True(await MigrationAppliedAsync(verify, "20260925161000_P7AdminEvents"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static async Task<List<string>> TableNamesAsync(AgentCoreDbContext db)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async Task<bool> MigrationAppliedAsync(AgentCoreDbContext db, string migrationId)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        if (command.Connection!.State != System.Data.ConnectionState.Open)
        {
            await command.Connection.OpenAsync();
        }

        command.CommandText =
            """
            SELECT EXISTS(
                SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = $id);
            """;
        var id = command.CreateParameter();
        id.ParameterName = "$id";
        id.Value = migrationId;
        command.Parameters.Add(id);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) != 0;
    }

    private static async Task ClearP7MigrationHistoryFromLifecycleAsync(AgentCoreDbContext db) =>
        await db.Database.ExecuteSqlRawAsync(
            """
            DELETE FROM "__EFMigrationsHistory"
            WHERE "MigrationId" IN (
                '20260925071140_P7DefinitionLifecycle',
                '20260925084907_P7DefinitionResources',
                '20260925101355_P7ManagedAgentInstance',
                '20260925101918_P7ManagedAgentInstanceRevisionToken',
                '20260925103832_P7PinnedPersonaRevision',
                '20260925150254_P7DefinitionDraftEvaluation',
                '20260925161000_P7AdminEvents');
            """);

    private static async Task<bool> ColumnIsNullableAsync(AgentCoreDbContext db, string table, string column)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        if (command.Connection!.State != System.Data.ConnectionState.Open)
        {
            await command.Connection.OpenAsync();
        }

        command.CommandText = $"PRAGMA table_info(\"{table}\");";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.Ordinal))
            {
                return reader.GetInt64(3) == 0;
            }
        }

        return false;
    }

    private static async Task<bool> ColumnExistsAsync(AgentCoreDbContext db, string table, string column)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        if (command.Connection!.State != System.Data.ConnectionState.Open)
        {
            await command.Connection.OpenAsync();
        }

        command.CommandText = $"PRAGMA table_info(\"{table}\");";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<bool> IndexExistsAsync(AgentCoreDbContext db, string indexName)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        if (command.Connection!.State != System.Data.ConnectionState.Open)
        {
            await command.Connection.OpenAsync();
        }

        command.CommandText =
            """
            SELECT EXISTS(
                SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = $name);
            """;
        var name = command.CreateParameter();
        name.ParameterName = "$name";
        name.Value = indexName;
        command.Parameters.Add(name);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) != 0;
    }

    private sealed class TestFactory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    {
        public AgentCoreDbContext CreateDbContext() => new(options);

        public ValueTask<AgentCoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CreateDbContext());
    }
}
