using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Identity;

namespace AgentCore.Infrastructure.Tests;

public sealed class InMemoryAgentDefinitionAdminStoreContractTests : AgentDefinitionAdminStoreContractTests
{
    protected override IAgentDefinitionAdminStore CreateStore() =>
        new InMemoryAgentDefinitionAdminStore(new SystemIdGenerator(TimeProvider.System));
}

public abstract class AgentDefinitionAdminStoreContractTests
{
    protected abstract IAgentDefinitionAdminStore CreateStore();

    [Fact]
    public async Task Draft_edit_publish_deprecate_contract()
    {
        var store = CreateStore();
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var candidate = SampleCandidate("demo-agent");

        var created = await store.CreateDraftAsync(
            new AgentDefinitionDraftCreate(
                "demo-agent",
                candidate,
                DefinitionDraftSourceKind.New,
                null,
                now),
            CancellationToken.None);
        Assert.Equal(1, created.Revision);

        var edited = await store.UpdateDraftAsync(
            new AgentDefinitionDraftUpdate(
                created.DraftId,
                1,
                candidate with { SystemInstructions = "Updated instructions for publish." },
                now.AddMinutes(1)),
            CancellationToken.None);
        Assert.Equal(2, edited.Revision);

        await Assert.ThrowsAsync<AgentCoreException>(() =>
            store.UpdateDraftAsync(
                new AgentDefinitionDraftUpdate(
                    created.DraftId,
                    1,
                    candidate,
                    now.AddMinutes(2)),
                CancellationToken.None).AsTask());

        var publication = await store.PublishDraftAsync(
            new AgentDefinitionDraftPublish(created.DraftId, 2, [], now.AddMinutes(3)),
            CancellationToken.None);
        Assert.Equal(1, publication.Version);
        Assert.Equal("Updated instructions for publish.", publication.Payload.SystemInstructions);

        var editedAgain = await store.UpdateDraftAsync(
            new AgentDefinitionDraftUpdate(
                created.DraftId,
                3,
                candidate with { SystemInstructions = "Second publication body." },
                now.AddMinutes(4)),
            CancellationToken.None);
        var secondPublication = await store.PublishDraftAsync(
            new AgentDefinitionDraftPublish(editedAgain.DraftId, editedAgain.Revision, [1], now.AddMinutes(5)),
            CancellationToken.None);
        Assert.Equal(2, secondPublication.Version);

        var deprecated = await store.DeprecatePublicationAsync(
            new AgentDefinitionPublicationDeprecate("demo-agent", 1, 1, now.AddMinutes(5)),
            CancellationToken.None);
        Assert.Equal(DefinitionPublicationStatus.Deprecated, deprecated.Status);
        Assert.NotNull(await store.GetPublicationAsync("demo-agent", 1));
    }

    [Fact]
    public async Task Composite_store_resolves_durable_publication_alongside_built_ins()
    {
        var builtIns = new FileAgentDefinitionStore(FindAgents(), SyntheticProviderAliases.Default);
        var admin = CreateStore();
        var composite = new CompositeAgentDefinitionStore(builtIns, admin);
        var examiner = await builtIns.GetAsync("examiner", 1);
        Assert.NotNull(examiner);

        var occupied = (await builtIns.ListAsync())
            .Where(item => string.Equals(item.Id, "examiner", StringComparison.Ordinal))
            .Select(item => item.Version)
            .ToArray();

        var draft = await admin.CreateDraftAsync(
            new AgentDefinitionDraftCreate(
                "examiner",
                AgentDefinitionCandidate.FromDefinition(examiner!) with
                {
                    SystemInstructions = examiner!.SystemInstructions + "\nDurable overlay."
                },
                DefinitionDraftSourceKind.ForkBuiltIn,
                1,
                DateTimeOffset.Parse("2026-01-02T00:00:00Z")),
            CancellationToken.None);

        var published = await admin.PublishDraftAsync(
            new AgentDefinitionDraftPublish(
                draft.DraftId,
                draft.Revision,
                occupied,
                DateTimeOffset.Parse("2026-01-02T00:01:00Z")),
            CancellationToken.None);
        Assert.Equal(occupied.Max() + 1, published.Version);

        var exactBuiltin = await composite.GetAsync("examiner", 1);
        var exactDurable = await composite.GetAsync("examiner", published.Version);
        Assert.NotNull(exactBuiltin);
        Assert.NotNull(exactDurable);
        Assert.DoesNotContain("Durable overlay.", exactBuiltin!.SystemInstructions, StringComparison.Ordinal);
        Assert.Contains("Durable overlay.", exactDurable!.SystemInstructions, StringComparison.Ordinal);
    }

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
}
