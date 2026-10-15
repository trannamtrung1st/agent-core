using AgentCore.Application.Sessions;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;

public sealed class CanonicalStartupMigrationTests
{
    [Fact]
    public async Task Fresh_baseline_reopens_without_file_rows_or_changing_independent_profile_revision()
    {
        var path = Path.Combine(Path.GetTempPath(), $"canonical-fresh-{Guid.NewGuid():N}.db");
        var factory = new ContextFactory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options);
        try
        {
            await new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync();
            await using (var db = factory.CreateDbContext())
                await db.Database.ExecuteSqlRawAsync("INSERT INTO UserProfiles (ProfileId, PreferencesJson, Revision, UpdatedAtUtc) VALUES ('preserved-owner', '{{}}', 7, 123456)");
            await new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync();
            await using var reopened = factory.CreateDbContext();
            Assert.Equal(["20261015154322_CanonicalFilesystemBaseline"], await reopened.Database.GetAppliedMigrationsAsync());
            var profile = await reopened.Profiles.SingleAsync();
            Assert.Equal(7, profile.Revision); Assert.Equal("{}",profile.PreferencesJson); Assert.Equal(123456,profile.UpdatedAtUtc);
            Assert.DoesNotContain(reopened.Model.GetEntityTypes(), type => type.GetTableName() == "AgentWorkspaceItems");
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(path); }
    }

    [Theory]
    [InlineData("AgentWorkspaceItems",false)]
    [InlineData("Sessions",false)]
    [InlineData("UserProfiles",false)]
    [InlineData("OtherStorage",false)]
    [InlineData("OtherStorage",true)]
    public async Task Legacy_untracked_and_unknown_schemas_require_reset_without_mutating_the_selected_database(string table,bool history)
    {
        var path = Path.Combine(Path.GetTempPath(), $"canonical-refusal-{Guid.NewGuid():N}.db");
        var factory = new ContextFactory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options);
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = $"CREATE TABLE \"{table}\" (Id TEXT PRIMARY KEY, Bytes BLOB); INSERT INTO \"{table}\" VALUES ('retained',X'00FF80');";
                await command.ExecuteNonQueryAsync();
                if (history)
                {
                    command.CommandText = "CREATE TABLE __EFMigrationsHistory (MigrationId TEXT PRIMARY KEY, ProductVersion TEXT NOT NULL); INSERT INTO __EFMigrationsHistory VALUES ('20261008063000_RetireLegacyExecution','10.0.12');";
                    await command.ExecuteNonQueryAsync();
                }
            }
            var before = await File.ReadAllBytesAsync(path);
            var error = await Assert.ThrowsAsync<AgentCoreException>(() => new SqliteMemoryStore(factory,TimeProvider.System).EnsureCreatedAsync().AsTask());
            Assert.Contains("reset",error.Message,StringComparison.OrdinalIgnoreCase);
            Assert.Equal(before,await File.ReadAllBytesAsync(path));
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(path); }
    }

    private sealed class ContextFactory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    {
        public AgentCoreDbContext CreateDbContext() => new(options);
    }
}
