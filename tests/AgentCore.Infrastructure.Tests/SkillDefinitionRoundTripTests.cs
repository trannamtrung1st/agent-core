using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;

public sealed class SkillDefinitionRoundTripTests
{
    [Fact]
    public async Task Built_in_files_without_skills_still_load()
    {
        var store = new FileAgentDefinitionStore(FindAgents(), SyntheticProviderAliases.Default);
        var definitions = await store.ListAsync();
        Assert.NotEmpty(definitions);
        var skillVersions = new[] { 11, 12, 13 };
        Assert.All(
            definitions.Where(definition =>
                (definition.Id != "general-assistant" || !skillVersions.Contains(definition.Version))
                && definition.Id != "secretary"),
            definition => Assert.Empty(definition.SkillList));
        foreach (var version in skillVersions)
        {
            var skill = Assert.Single(
                Assert.Single(
                        definitions,
                        definition => definition.Id == "general-assistant" && definition.Version == version)
                    .SkillList);
            Assert.Equal("browser.record.lookup", skill.Id);
        }

        var secretarySkills = Assert.Single(definitions, definition => definition.Id == "secretary" && definition.Version == 1)
            .SkillList
            .Select(skill => skill.Id)
            .ToArray();
        Assert.Equal(
            ["store.product.manage", "store.order.review", "store.inventory.review", "store.promotion.manage", "store.daily.review"],
            secretarySkills);
    }

    [Fact]
    public async Task File_store_round_trips_present_skills_and_omits_a_missing_list()
    {
        var directory = Directory.CreateTempSubdirectory("agent-core-skills-");
        try
        {
            var withSkills = Definition() with
            {
                Skills =
                [
                    new SkillSpec("refund.handle", "Refunds", "", "Confirm the order.", ["refund"], ["chat.respond"], [])
                ]
            };
            var withoutSkills = Definition() with { Id = "plain-agent" };
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "skilled.json"), Json(withSkills));
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "plain.json"), Json(withoutSkills));
            Assert.DoesNotContain("skills", await File.ReadAllTextAsync(Path.Combine(directory.FullName, "plain.json")), StringComparison.Ordinal);

            var store = new FileAgentDefinitionStore(directory.FullName, SyntheticProviderAliases.Default);
            var loaded = await store.GetAsync("examiner", 1);
            Assert.Equal("refund.handle", Assert.Single(loaded!.SkillList).Id);
            Assert.Empty((await store.GetAsync("plain-agent", 1))!.SkillList);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Admin_draft_and_publication_round_trip_skills()
    {
        await ForEachAdminStoreAsync(async admin =>
        {
            var skills = new[]
            {
                new SkillSpec("refund.handle", "Refunds", "Orders.", "Confirm the order.", ["refund"], ["chat.respond"], ["notes/refund.md"])
            };
            var now = DateTimeOffset.Parse("2026-09-30T12:00:00Z");
            var draft = await admin.CreateDraftAsync(
                new AgentDefinitionDraftCreate(
                    "skill-guide",
                    Candidate(skills),
                    DefinitionDraftSourceKind.New,
                    null,
                    now),
                CancellationToken.None);
            var stored = await admin.GetDraftAsync(draft.DraftId, CancellationToken.None);
            Assert.Equal(Shapes(skills), Shapes(stored!.Candidate.SkillList));

            var publication = await admin.PublishDraftAsync(
                new AgentDefinitionDraftPublish(draft.DraftId, draft.Revision, [], now),
                CancellationToken.None);
            var loaded = await admin.GetPublicationAsync("skill-guide", publication.Version, CancellationToken.None);
            Assert.Equal(Shapes(skills), Shapes(loaded!.Payload.SkillList));
        });
    }

    [Fact]
    public async Task Session_snapshot_round_trips_skills_in_memory_and_sqlite()
    {
        var snapshot = Snapshot();
        var memory = new InMemoryMemoryStore();
        await memory.SaveAsync(snapshot, 0);
        Assert.Equal(Shapes(snapshot.Definition.SkillList), Shapes((await memory.LoadAsync(snapshot.SessionId))!.Definition.SkillList));

        var path = Path.Combine(Path.GetTempPath(), $"agent-core-skills-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        var factory = new SqliteContextFactory(options);
        try
        {
            var store = new SqliteMemoryStore(factory, TimeProvider.System);
            await store.EnsureCreatedAsync();
            await store.SaveAsync(snapshot, 0);
            var loaded = await store.LoadAsync(snapshot.SessionId);
            Assert.Equal(Shapes(snapshot.Definition.SkillList), Shapes(loaded!.Definition.SkillList));
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

    private static async Task ForEachAdminStoreAsync(Func<IAgentDefinitionAdminStore, Task> exercise)
    {
        await exercise(new InMemoryAgentDefinitionAdminStore(new SystemIdGenerator(TimeProvider.System)));

        var path = Path.Combine(Path.GetTempPath(), $"agent-core-skill-admin-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        var factory = new SqliteContextFactory(options);
        try
        {
            await new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync();
            await exercise(new SqliteAgentDefinitionAdminStore(
                factory,
                new SystemIdGenerator(TimeProvider.System),
                new InMemoryDefinitionResourceContentStore()));
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

    private static string[] Shapes(IReadOnlyList<SkillSpec> skills) =>
        skills.Select(skill =>
                $"{skill.Id}|{skill.Name}|{skill.Description}|{skill.Procedure}|{string.Join(",", skill.ActivationKeywords)}|{string.Join(",", skill.RequiredCapabilities)}|{string.Join(",", skill.ResourcePaths)}")
            .ToArray();

    private static string Json(AgentDefinition definition) =>
        JsonSerializer.Serialize(definition, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    private static AgentDefinitionCandidate Candidate(IReadOnlyList<SkillSpec> skills) =>
        new(
            1,
            "skill-guide",
            new AgentIdentity("Sam", "Guide", "Helps with demos.", "Calm"),
            ["Help the user"],
            "You are a demo agent.",
            new BehaviorPolicy("acknowledgeThenContinue", true, true),
            new ConversationPolicy("concise", true, "en", 512),
            new InitiativePolicy(false, 30_000, 60_000, 1, []),
            new VoiceConfiguration(false, "alloy", 1.0),
            new ProviderPreferences("primary-llm", null, null),
            new Dictionary<string, string>(),
            Skills: skills);

    private static AgentDefinition Definition() =>
        new(
            1,
            "examiner",
            1,
            new AgentIdentity("Alex", "role", "desc", "tone"),
            ["goal"],
            "instructions",
            new BehaviorPolicy("acknowledgeThenContinue", true, true),
            new ConversationPolicy("concise", true, "en", 256),
            new InitiativePolicy(true, 8000, 30000, 1, ["longSilence"]),
            new VoiceConfiguration(false, "default", 1.0),
            new ProviderPreferences("primary-llm", null, null),
            new Dictionary<string, string>());

    private static SessionSnapshot Snapshot() =>
        new(
            1,
            Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842"),
            1,
            Definition() with
            {
                Skills = [new SkillSpec("refund.handle", "Refunds", "", "Confirm the order.", ["refund"], ["chat.respond"], [])]
            },
            SessionMode.Text,
            null,
            SessionStatus.Created,
            [],
            string.Empty,
            0,
            null,
            null,
            new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero));

    private static string FindAgents()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "agents");
            if (File.Exists(Path.Combine(candidate, "examiner.json")))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("agents directory was not found.");
    }

    private sealed class SqliteContextFactory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    {
        public AgentCoreDbContext CreateDbContext() => new(options);

        public Task<AgentCoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
