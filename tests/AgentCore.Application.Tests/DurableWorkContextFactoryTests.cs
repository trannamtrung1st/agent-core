using AgentCore.Application.Memory;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Work;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Memory;
using AgentCore.Domain.Work;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class DurableWorkContextFactoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 6, 0, 0, TimeSpan.Zero);
    private static readonly Guid InstanceId = Guid.Parse("019944af-00a1-7000-8000-000000000001");
    private static readonly Guid ProfileId = Guid.Parse("019944af-00a2-7000-8000-000000000002");
    private static readonly Guid SourceId = Guid.Parse("019944af-00a3-7000-8000-000000000003");

    [Fact]
    public async Task CreateAsync_allows_archived_instance_for_pinned_work()
    {
        var harness = await SeedHarnessAsync(AgentInstanceLifecycle.Archived, SampleDefinitions.Examiner, 1, "Alex");
        var item = NewWorkItem(
            harness.Definition.Id,
            harness.Definition.Version,
            "Pinned Persona");

        var context = await harness.Factory.CreateAsync(item, CancellationToken.None);
        Assert.NotNull(context.Persona);
        Assert.Equal("Pinned Persona", context.Persona.Name);
    }

    [Fact]
    public async Task CreateAsync_uses_pinned_definition_version_when_instance_advanced()
    {
        var v1 = SampleDefinitions.Examiner;
        var v2 = v1 with { Version = 2, Identity = v1.Identity with { Name = "Live Name" } };
        var harness = await SeedHarnessAsync(AgentInstanceLifecycle.Active, v2, 2, "Live Name");
        var item = NewWorkItem(v1.Id, 1, "Historical Riley");

        var context = await harness.Factory.CreateAsync(item, CancellationToken.None);
        Assert.Equal(1, context.Definition.Version);
        Assert.Equal("Alex", context.Definition.Identity.Name);
        Assert.NotNull(context.Persona);
        Assert.Equal("Historical Riley", context.Persona.Name);
    }

    [Fact]
    public async Task CreateAsync_persona_comes_from_provenance_not_live_instance_identity()
    {
        var definition = SampleDefinitions.Examiner;
        var harness = await SeedHarnessAsync(AgentInstanceLifecycle.Active, definition, 1, "Live Instance Name");
        var item = NewWorkItem(definition.Id, definition.Version, "Provenance Persona");

        var context = await harness.Factory.CreateAsync(item, CancellationToken.None);
        Assert.NotNull(context.Persona);
        Assert.Equal("Provenance Persona", context.Persona.Name);
        Assert.Equal("Alex", context.Definition.Identity.Name);
    }

    private static WorkItem NewWorkItem(string definitionId, int definitionVersion, string personaName) =>
        WorkItem.Create(
            Guid.Parse("019944af-00a4-7000-8000-000000000004"),
            new WorkOwner(InstanceId, ProfileId),
            new WorkProvenance(
                SourceId,
                WorkSourceKind.Schedule,
                null,
                Guid.Parse("019944af-00a5-7000-8000-000000000005"),
                null,
                $"source|{SourceId:N}",
                Now,
                Now,
                """{"instruction":"synthetic"}""",
                definitionId,
                definitionVersion,
                personaName),
            new WorkModelPin("scripted-alpha", "primary-llm", "scripted-alpha", "medium"),
            3,
            Now);

    private static async Task<Harness> SeedHarnessAsync(
        AgentInstanceLifecycle lifecycle,
        AgentDefinition liveDefinition,
        int instanceDefinitionVersion,
        string instanceDisplayName)
    {
        var clock = new FakeTimeProvider(Now);
        var sessions = new InMemoryMemoryStore();
        await sessions.SaveProfileAsync(
            new UserProfile(ProfileId, 1, new Dictionary<string, UserProfileValue>
            {
                ["preferredName"] = new("Operator", UserProfileValueSource.UserSet, Now)
            }, Now),
            0);

        var instances = new InMemoryAgentInstanceStore();
        await instances.InsertAsync(new AgentInstance(
            InstanceId,
            liveDefinition.Id,
            instanceDefinitionVersion,
            liveDefinition.Identity with { Name = instanceDisplayName },
            lifecycle,
            Now,
            Now,
            false));

        var definitions = new MultiVersionDefinitionStore(
            SampleDefinitions.Examiner,
            SampleDefinitions.Examiner with { Version = 2, Identity = SampleDefinitions.Examiner.Identity with { Name = "Live Name" } });
        var structured = new InMemoryStructuredMemoryStore();
        var memories = new StructuredMemoryService(structured, new SystemIdGenerator(clock), clock);
        var catalog = TestModelCatalogs.Synthetic();
        var factory = new DurableWorkContextFactory(
            instances,
            definitions,
            sessions,
            memories,
            catalog,
            new StaticLanguageModelResolver(new ScriptedLanguageModel(["ok"])),
            clock);
        return new Harness(factory, liveDefinition);
    }

    private sealed record Harness(DurableWorkContextFactory Factory, AgentDefinition Definition);

    private sealed class MultiVersionDefinitionStore(AgentDefinition v1, AgentDefinition v2) : IAgentDefinitionStore
    {
        public ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AgentDefinition>>([v1, v2]);

        public ValueTask<AgentDefinition?> GetAsync(string id, int? version = null, CancellationToken cancellationToken = default)
        {
            if (!string.Equals(id, v1.Id, StringComparison.Ordinal))
            {
                return ValueTask.FromResult<AgentDefinition?>(null);
            }

            if (version is null)
            {
                return ValueTask.FromResult<AgentDefinition?>(v2);
            }

            return version switch
            {
                1 => ValueTask.FromResult<AgentDefinition?>(v1),
                2 => ValueTask.FromResult<AgentDefinition?>(v2),
                _ => ValueTask.FromResult<AgentDefinition?>(null)
            };
        }
    }
}
