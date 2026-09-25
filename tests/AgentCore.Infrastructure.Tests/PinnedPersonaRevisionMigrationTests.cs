using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;

public sealed class PinnedPersonaRevisionMigrationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-06T00:00:00Z");

    [Fact]
    public async Task Upgrade_preserves_legacy_session_with_null_pinned_persona_revision()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-pin-rev-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        var factory = new ContextFactory(options);
        var sessionId = Guid.Parse("019944af-00d2-7000-8000-0000000000cc");
        try
        {
            await MigrationSessionSeed.CopyPersistedSessionAsync(
                factory,
                LegacySession(sessionId),
                MigrationSessionSeed.PrePinnedPersonaRevisionMigrationId);

            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.MigrateAsync();
            }

            var memory = new SqliteMemoryStore(factory, TimeProvider.System);
            var loaded = await memory.LoadAsync(sessionId);
            Assert.NotNull(loaded);
            Assert.Null(loaded!.PinnedPersonaRevision);
            Assert.Contains("legacy-pin-marker", loaded.Definition.SystemInstructions, StringComparison.Ordinal);
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
    public async Task Managed_pinned_persona_revision_survives_sqlite_reopen()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-pin-managed-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        var factory = new ContextFactory(options);
        var sessionId = Guid.Parse("019944af-00d3-7000-8000-0000000000dd");
        try
        {
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.MigrateAsync();
            }

            var memory = new SqliteMemoryStore(factory, TimeProvider.System);
            await memory.SaveAsync(
                ManagedPinnedSession(sessionId),
                0);

            var reopened = new SqliteMemoryStore(factory, TimeProvider.System);
            var loaded = await reopened.LoadAsync(sessionId);
            Assert.NotNull(loaded);
            Assert.Equal(2, loaded!.PinnedPersonaRevision);
            Assert.Equal("managed tone", loaded.PinnedPersona!.Tone);
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

    private static SessionSnapshot LegacySession(Guid sessionId) =>
        new(
            1,
            sessionId,
            1,
            SampleDefinition("legacy-pin-marker"),
            SessionMode.Text,
            null,
            SessionStatus.Created,
            [],
            string.Empty,
            0,
            null,
            null,
            Now,
            Now);

    private static SessionSnapshot ManagedPinnedSession(Guid sessionId) =>
        new(
            1,
            sessionId,
            1,
            SampleDefinition("managed"),
            SessionMode.Text,
            null,
            SessionStatus.Created,
            [],
            string.Empty,
            0,
            null,
            null,
            Now,
            Now,
            AgentInstanceId: Guid.Parse("019944af-00d3-7000-8000-000000000001"),
            PinnedPersona: new AgentIdentity("Alex", "Guide", "Helps.", "managed tone"),
            PinnedPersonaRevision: 2);

    private static AgentDefinition SampleDefinition(string marker) =>
        new(
            1,
            "examiner",
            1,
            new AgentIdentity("Alex", "role", "desc", "tone"),
            ["goal"],
            marker,
            new BehaviorPolicy("acknowledgeThenContinue", true, true),
            new ConversationPolicy("concise", true, "en", 512),
            new InitiativePolicy(false, 30_000, 60_000, 1, []),
            new VoiceConfiguration(false, "alloy", 1.0),
            new ProviderPreferences("primary-llm", null, null),
            new Dictionary<string, string>());

    private sealed class ContextFactory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    {
        public AgentCoreDbContext CreateDbContext() => new(options);

        public ValueTask<AgentCoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CreateDbContext());
    }
}
