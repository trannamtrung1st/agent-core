using AgentCore.Application.Sessions;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.Synthetic;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Tests;

public sealed class InMemoryAndScriptedTests
{
    [Fact]
    public async Task In_memory_store_rejects_stale_revision()
    {
        var store = new InMemoryMemoryStore();
        var snapshot = FirstSnapshot();
        await store.SaveAsync(snapshot, 0);
        var stale = snapshot with { Revision = 1, Summary = "changed" };
        var conflict = await Assert.ThrowsAsync<AgentCoreException>(() => store.SaveAsync(stale, 0).AsTask());
        Assert.Equal("Conflict", conflict.Code);
    }

    [Fact]
    public async Task In_memory_store_is_idempotent_for_same_revision_content()
    {
        var store = new InMemoryMemoryStore();
        var snapshot = FirstSnapshot();
        await store.SaveAsync(snapshot, 0);
        await store.SaveAsync(snapshot, 0);
        var loaded = await store.LoadAsync(snapshot.SessionId);
        Assert.Equal(snapshot.Revision, loaded!.Revision);
    }

    [Fact]
    public async Task Scripted_language_model_emits_deltas_then_completed()
    {
        var model = new ScriptedLanguageModel(["A", "B"]);
        var events = new List<Application.Ports.ModelGenerationEvent>();
        await foreach (var item in model.GenerateAsync(
                           new Application.Ports.ModelRequest(Guid.NewGuid(), [])))
        {
            events.Add(item);
        }

        Assert.Equal(3, events.Count);
        Assert.IsType<Application.Ports.ModelTextDelta>(events[0]);
        Assert.IsType<Application.Ports.ModelCompleted>(events[2]);
    }

    [Fact]
    public async Task File_store_loads_pinned_demo_definitions()
    {
        var directory = FindAgents();
        var store = new FileAgentDefinitionStore(directory);
        var examiner = await store.GetAsync("examiner");
        var support = await store.GetAsync("customer-support");
        Assert.NotNull(examiner);
        Assert.NotNull(support);
        Assert.Equal(1, examiner!.Version);
        Assert.Equal("Sam", support!.Identity.Name);
    }

    private static string FindAgents()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var agents = Path.Combine(dir.FullName, "agents");
            if (Directory.Exists(agents) && File.Exists(Path.Combine(agents, "examiner.json")))
            {
                return agents;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("agents/");
    }

    private static SessionSnapshot FirstSnapshot()
    {
        var now = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
        var definition = new Domain.Definitions.AgentDefinition(
            1,
            "examiner",
            1,
            new Domain.Definitions.AgentIdentity("Alex", "role", "desc", "tone"),
            ["goal"],
            "instructions",
            new Domain.Definitions.BehaviorPolicy("acknowledgeThenContinue", true, true),
            new Domain.Definitions.ConversationPolicy("concise", true, "en", 256),
            new Domain.Definitions.InitiativePolicy(true, 8000, 30000, 1, ["longSilence"]),
            new Domain.Definitions.VoiceConfiguration(false, "default", 1.0),
            new Domain.Definitions.ProviderPreferences("primary-llm", null, null),
            new Dictionary<string, string>());
        return new SessionSnapshot(
            1,
            Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842"),
            1,
            definition,
            SessionMode.Text,
            null,
            SessionStatus.Created,
            [],
            string.Empty,
            0,
            null,
            null,
            now,
            now);
    }
}
