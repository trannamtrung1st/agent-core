using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;

public sealed class DefinitionLifecycleMigrationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-06T00:00:00Z");
    private const string PreP7BMigrationId = "20260924155535_WorkSideEffectToolCallId";

    [Fact]
    public async Task Startup_rejects_pre_p7b_baseline_without_changing_the_session()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-p7b-migrate-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        var factory = new ContextFactory(options);
        var sessionId = Guid.Parse("019944af-00d1-7000-8000-0000000000bb");
        try
        {
            await MigrationSessionSeed.CopyPersistedSessionAsync(
                factory,
                PreP7BSession(sessionId),
                PreP7BMigrationId);

            var error = await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(() =>
                new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync().AsTask());
            Assert.Contains("reset", error.Message, StringComparison.OrdinalIgnoreCase);
            await using var verify = await factory.CreateDbContextAsync();
            var json = await verify.Database.SqlQuery<string>($"SELECT DefinitionJson AS Value FROM Sessions WHERE SessionId = {sessionId.ToString("D")}").SingleAsync();
            Assert.Contains("pre-p7b-session-marker", json, StringComparison.Ordinal);
            Assert.DoesNotContain("20261008063000_RetireLegacyExecution", await verify.Database.GetAppliedMigrationsAsync());
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

    private static SessionSnapshot PreP7BSession(Guid sessionId) =>
        new(
            1,
            sessionId,
            1,
            new AgentDefinition(
                1,
                "examiner",
                1,
                new AgentIdentity("Alex", "role", "desc", "tone"),
                ["goal"],
                "pre-p7b-session-marker",
                new BehaviorPolicy("acknowledgeThenContinue", true, true),
                new ConversationPolicy("concise", true, "en", 512),
                new InitiativePolicy(false, 30_000, 60_000, 1, []),
                new VoiceConfiguration(false, "alloy", 1.0),
                new ProviderPreferences("primary-llm", null, null),
                new Dictionary<string, string>()),
            SessionMode.Text,
            null,
            SessionStatus.Created,
            [],
            string.Empty,
            0,
            null,
            null,
            Now,
            Now, AgentInstanceId: Guid.NewGuid());

    private static AgentDefinitionCandidate SampleCandidate(string definitionId) =>
        new(
            1,
            definitionId,
            new AgentIdentity("Demo", "Guide", "Helps with demos.", "Calm"),
            ["Help the user"],
            "You are a demo agent.",
            new BehaviorPolicy("acknowledgeThenContinue", true, true),
            new ConversationPolicy("concise", true, "en", 512),
            new InitiativePolicy(false, 30_000, 60_000, 1, []),
            new VoiceConfiguration(false, "alloy", 1.0),
            new ProviderPreferences("primary-llm", null, null),
            new Dictionary<string, string>());

    private static async Task<List<string>> TableNamesAsync(AgentCoreDbContext db)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        if (command.Connection!.State != System.Data.ConnectionState.Open)
        {
            await command.Connection.OpenAsync();
        }

        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private sealed class ContextFactory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    {
        public AgentCoreDbContext CreateDbContext() => new(options);

        public ValueTask<AgentCoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CreateDbContext());
    }
}
