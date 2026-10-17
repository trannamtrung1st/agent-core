using AgentCore.Application.Sessions;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;

public sealed class CanonicalSchemaRefusalTests
{
    [Fact]
    public async Task Reopen_rejects_missing_agent_definition_publications_table_without_repair()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-canonical-partial-lifecycle-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        var factory = new TestFactory(options);
        try
        {
            await new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync();
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS \"AgentDefinitionPublications\";");
            }

            var error = await Assert.ThrowsAsync<AgentCoreException>(() =>
                new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync().AsTask());
            Assert.Contains("reset", error.Message, StringComparison.OrdinalIgnoreCase);
            await using var verify = await factory.CreateDbContextAsync();
            Assert.Equal(verify.Database.GetMigrations(), await verify.Database.GetAppliedMigrationsAsync());
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
    public async Task Reopen_rejects_empty_malformed_agent_definition_publications_columns_without_repair()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-canonical-malformed-publications-{Guid.NewGuid():N}.db");
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
            }

            var error = await Assert.ThrowsAsync<AgentCoreException>(() =>
                new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync().AsTask());
            Assert.Contains("reset", error.Message, StringComparison.OrdinalIgnoreCase);
            await using var verify = await factory.CreateDbContextAsync();
            Assert.Equal(verify.Database.GetMigrations(), await verify.Database.GetAppliedMigrationsAsync());
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
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-canonical-nonempty-malformed-{Guid.NewGuid():N}.db");
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
            }

            var error = await Assert.ThrowsAsync<AgentCoreException>(() =>
                new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync().AsTask());
            Assert.Contains("incomplete or incompatible shape", error.Message, StringComparison.Ordinal);
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
    public async Task Reopen_refuses_incompatible_pinned_persona_nullability_without_repair()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-canonical-pinned-nullability-{Guid.NewGuid():N}.db");
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
            }

            var error = await Assert.ThrowsAsync<AgentCoreException>(() =>
                new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync().AsTask());
            Assert.Contains("reset", error.Message, StringComparison.OrdinalIgnoreCase);

            await using var verify = await factory.CreateDbContextAsync();
            Assert.Equal(verify.Database.GetMigrations(), await verify.Database.GetAppliedMigrationsAsync());
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
    public async Task Reopen_refuses_incompatible_managed_instance_nullability_without_repair()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-canonical-managed-nullability-{Guid.NewGuid():N}.db");
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
            }

            var error = await Assert.ThrowsAsync<AgentCoreException>(() =>
                new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync().AsTask());
            Assert.Contains("reset", error.Message, StringComparison.OrdinalIgnoreCase);

            await using var verify = await factory.CreateDbContextAsync();
            Assert.Equal(verify.Database.GetMigrations(), await verify.Database.GetAppliedMigrationsAsync());
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
    public async Task Reopen_rejects_missing_admin_events_operation_id_index_without_repair()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-canonical-partial-admin-events-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        var factory = new TestFactory(options);
        try
        {
            await new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync();
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.ExecuteSqlRawAsync("DROP INDEX IF EXISTS \"IX_AdminEvents_OperationId\";");
            }

            var error = await Assert.ThrowsAsync<AgentCoreException>(() =>
                new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync().AsTask());
            Assert.Contains("reset", error.Message, StringComparison.OrdinalIgnoreCase);
            await using var verify = await factory.CreateDbContextAsync();
            Assert.False(await IndexExistsAsync(verify, "IX_AdminEvents_OperationId"));
            Assert.Equal(verify.Database.GetMigrations(), await verify.Database.GetAppliedMigrationsAsync());
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
