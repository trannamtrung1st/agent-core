using AgentCore.Application.Sessions;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Tests;

public sealed class CanonicalStartupMigrationTests
{
    [Theory]
    [InlineData("20261008063000_RetireLegacyExecution")]
    [InlineData("20261008115119_AutomationDestinations")]
    public async Task Startup_applies_pending_canonical_migrations_preserves_data_and_reopens(string previousMigration)
    {
        var path = Path.Combine(Path.GetTempPath(), $"canonical-startup-{Guid.NewGuid():N}.db");
        var factory = new ContextFactory(new DbContextOptionsBuilder<AgentCoreDbContext>()
            .UseSqlite($"Data Source={path}").Options);
        try
        {
            await using (var db = factory.CreateDbContext())
            {
                await db.GetService<IMigrator>().MigrateAsync(previousMigration);
                await db.Database.ExecuteSqlRawAsync("""
                    INSERT INTO UserProfiles (ProfileId, PreferencesJson, Revision, UpdatedAtUtc)
                    VALUES ('preserved-owner', '{{}}', 7, 123456);
                    """);
            }

            await new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync();
            await new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync();

            await using var verify = factory.CreateDbContext();
            Assert.Empty(await verify.Database.GetPendingMigrationsAsync());
            var profile = await verify.Profiles.SingleAsync();
            Assert.Equal("preserved-owner", profile.ProfileId);
            Assert.Equal(7, profile.Revision);
            Assert.Equal("{}", profile.PreferencesJson);
            Assert.Equal(123456, profile.UpdatedAtUtc);
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(path); }
    }

    [Fact]
    public async Task Startup_does_not_stamp_an_untracked_older_canonical_schema()
    {
        var path = Path.Combine(Path.GetTempPath(), $"canonical-untracked-{Guid.NewGuid():N}.db");
        var factory = new ContextFactory(new DbContextOptionsBuilder<AgentCoreDbContext>()
            .UseSqlite($"Data Source={path}").Options);
        try
        {
            await using (var db = factory.CreateDbContext())
            {
                await db.GetService<IMigrator>().MigrateAsync("20261008063000_RetireLegacyExecution");
                await db.Database.ExecuteSqlRawAsync("DROP TABLE __EFMigrationsHistory;");
            }
            var error = await Assert.ThrowsAsync<AgentCoreException>(() =>
                new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync().AsTask());
            Assert.Contains("reset", error.Message, StringComparison.OrdinalIgnoreCase);
            await using var verify = factory.CreateDbContext();
            Assert.Empty(await verify.Database.GetAppliedMigrationsAsync());
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(path); }
    }

    private sealed class ContextFactory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    {
        public AgentCoreDbContext CreateDbContext() => new(options);
        public ValueTask<AgentCoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CreateDbContext());
    }
}
