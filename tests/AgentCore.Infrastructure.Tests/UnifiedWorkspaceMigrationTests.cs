using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Tests;

public sealed class UnifiedWorkspaceMigrationTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("compatibility")]
    public async Task Legacy_owners_require_reset_without_converting_or_deleting_data(string legacy)
    {
        var path = Path.Combine(Path.GetTempPath(), $"workspace-migration-{Guid.NewGuid():N}.db");
        var factory = new ContextFactory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options);
        try
        {
            await using var db = factory.CreateDbContext();
            await db.GetService<IMigrator>().MigrateAsync("20261006095536_ConfigurableContinuityCadence");
            var owner = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var definition = Definition();
            var snapshot = new SessionSnapshot(1, Guid.NewGuid(), 1, definition, SessionMode.Text, null,
                SessionStatus.Created, [], "sentinel", 0, null, null, now, now, owner);
            var memory = new SqliteMemoryStore(factory, TimeProvider.System);
            await memory.SaveAsync(snapshot, 0);
            if (legacy == "compatibility")
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO AgentInstances (InstanceId, DefinitionId, ActiveVersion, PersonaJson, Lifecycle, CreatedAtUtc, UpdatedAtUtc, Compatibility, Revision, PersonaRevision) VALUES ({owner.ToString("D")}, 'examiner', 1, '{{}}', 'Active', 0, 0, 1, 1, 1)");
            }
            else
            {
                await db.Database.ExecuteSqlRawAsync(legacy == "null" ? "UPDATE Sessions SET AgentInstanceId = NULL" : "UPDATE Sessions SET AgentInstanceId = ''");
            }
            var error = await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync());
            Assert.Contains("Legacy data reset required", error.Message, StringComparison.Ordinal);
            Assert.Equal(1, await db.Sessions.CountAsync());
            Assert.Equal("sentinel", (await db.Set<SnapshotRecord>().SingleAsync()).Summary);
            Assert.DoesNotContain("20261007014134_UnifiedAgentWorkspace", await db.Database.GetAppliedMigrationsAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    private static AgentDefinition Definition() => new(1, "examiner", 1,
        new AgentIdentity("Alex", "Examiner", "Help", "Calm"), ["Help"], "Help",
        new BehaviorPolicy("acknowledgeThenContinue", true, true),
        new ConversationPolicy("concise", true, "en", 256), new InitiativePolicy(false, 30000, 60000, 1, []),
        new VoiceConfiguration(false, "default", 1), new ProviderPreferences("primary-llm", null, null), new Dictionary<string, string>());

    private sealed class ContextFactory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    {
        public AgentCoreDbContext CreateDbContext() => new(options);
        public Task<AgentCoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
