using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Tests;

public sealed class InstanceSkillsStartupTests
{
    [Fact]
    public async Task Owner_identity_migration_preserves_existing_skill_and_allows_another_owner_to_reuse_its_id()
    {
        var path = Path.Combine(Path.GetTempPath(), $"skills-owner-{Guid.NewGuid():N}.db");
        var factory = new ContextFactory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options);
        try
        {
            await using (var old = factory.CreateDbContext())
                await old.GetService<IMigrator>().MigrateAsync("20261007114116_InstanceSkillsCutover");
            var store = new SqliteAgentInstanceStore(factory, new AgentCore.Infrastructure.Identity.SystemIdGenerator(TimeProvider.System));
            var first = Guid.NewGuid(); var second = Guid.NewGuid(); var skillId = Guid.NewGuid().ToString("D"); var now = DateTimeOffset.UtcNow;
            foreach (var owner in new[] { first, second })
                await store.InsertAsync(new AgentCore.Domain.Definitions.AgentInstance(owner, "examiner", 1,
                    new("Alex", "Examiner", "Help", "Calm"), AgentCore.Domain.Definitions.AgentInstanceLifecycle.Active, now, now));
            var existing = new AgentInstanceSkill(skillId, first, "Review", "Review evidence", "Retained procedure", SkillProjection.OnDemand, true, [], 1, now, now, SkillAuthor.Admin);
            await store.MutateSkillsAsync(new(first, 1, InstanceSkill: existing));
            await using (var migrated = factory.CreateDbContext()) await migrated.Database.MigrateAsync();
            var retained = Assert.Single((await store.ReadSkillsAsync(first)).InstanceSkills);
            Assert.Equal(existing.SkillId, retained.SkillId); Assert.Equal(existing.Revision, retained.Revision);
            Assert.Equal(existing.Procedure, retained.Procedure); Assert.Equal(existing.AgentInstanceId, retained.AgentInstanceId);
            await store.MutateSkillsAsync(new(second, 1, InstanceSkill: existing with { AgentInstanceId = second }));
            Assert.Equal(skillId, Assert.Single((await store.ReadSkillsAsync(second)).InstanceSkills).SkillId);
            Assert.Equal("Retained procedure", Assert.Single((await store.ReadSkillsAsync(first)).InstanceSkills).Procedure);
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(path); }
    }

    [Theory]
    [InlineData("session", "projection")]
    [InlineData("session", "defaultEnabled")]
    [InlineData("publication", "projection")]
    [InlineData("publication", "defaultEnabled")]
    [InlineData("draft", "projection")]
    [InlineData("draft", "defaultEnabled")]
    public async Task Incompatible_stored_skill_contract_requires_reset_without_changing_data(string location, string missing)
    {
        var path = Path.Combine(Path.GetTempPath(), $"skills-startup-{Guid.NewGuid():N}.db");
        var factory = new ContextFactory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options);
        try
        {
            var memory = new SqliteMemoryStore(factory, TimeProvider.System);
            await memory.EnsureCreatedAsync();
            var definition = Definition() with { Skills = [new("review", "Review", "Review evidence", "Review carefully", SkillProjection.OnDemand, true, [], [])] };
            var json = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(definition,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)))!;
            json["skills"]![0]!.AsObject().Remove(missing);
            var legacy = json.ToJsonString();
            await using var db = factory.CreateDbContext();
            if (location == "session")
            {
                var now = DateTimeOffset.UtcNow;
                await memory.SaveAsync(new SessionSnapshot(1, Guid.NewGuid(), 1, definition, SessionMode.Text, null,
                    SessionStatus.Created, [], "sentinel", 0, null, null, now, now, Guid.NewGuid()), 0);
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Sessions SET DefinitionJson = {legacy}");
            }
            else if (location == "publication")
            {
                db.AgentDefinitionPublications.Add(new() { DefinitionId = "examiner", Version = 1, PayloadJson = legacy });
                await db.SaveChangesAsync();
            }
            else
            {
                db.AgentDefinitionDrafts.Add(new() { DraftId = Guid.NewGuid().ToString("D"), DefinitionId = "examiner", CandidateJson = legacy });
                await db.SaveChangesAsync();
            }
            var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await memory.EnsureCreatedAsync());
            Assert.Contains("Legacy Skill data reset required", error.Message);
            Assert.Contains("back up", error.Message);
            var retained = location == "session" ? (await db.Sessions.SingleAsync()).DefinitionJson
                : location == "publication" ? (await db.AgentDefinitionPublications.SingleAsync()).PayloadJson
                : (await db.AgentDefinitionDrafts.SingleAsync()).CandidateJson;
            Assert.Equal(legacy, retained);
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
