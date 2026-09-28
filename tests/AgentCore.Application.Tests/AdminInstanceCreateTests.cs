using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Admin;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class AdminInstanceCreateTests
{
    [Fact]
    public async Task CreateManagedAsync_stores_the_definition_persona_in_one_create_event()
    {
        var fixture = CreateFixture();
        var created = await fixture.Service.CreateManagedAsync("examiner", 1, CancellationToken.None);

        Assert.Equal(DefinitionPersona(), created.Persona);
        Assert.Equal(AgentInstanceLifecycle.Active, created.Lifecycle);
        Assert.Equal(1, created.ActiveVersion);
        Assert.Equal(1, created.PersonaRevision);
        var stored = await fixture.Instances.FindAsync(created.InstanceId, CancellationToken.None);
        Assert.Equal(DefinitionPersona(), stored!.Persona);

        var createdEvent = Assert.Single(await ListInstanceEventsAsync(fixture, created.InstanceId));
        Assert.Equal(AdminEventOperationKind.ManagedInstanceCreated, createdEvent.Operation);
        using var summary = JsonDocument.Parse(createdEvent.SummaryJson);
        Assert.Equal("Default", summary.RootElement.GetProperty("personaSource").GetString());
        Assert.Equal(
            AdminPersonaHistoryFingerprint.Compute(DefinitionPersona()),
            summary.RootElement.GetProperty("personaFingerprint").GetString());
    }

    [Fact]
    public async Task CreateManagedAsync_stores_a_custom_persona_without_a_following_persona_change()
    {
        var fixture = CreateFixture();
        var persona = new AgentIdentity("Casey", "Guide", "A field guide.", "Direct");
        var created = await fixture.Service.CreateManagedAsync("examiner", 1, persona, CancellationToken.None);

        Assert.Equal(persona, created.Persona);
        Assert.Equal(1, created.PersonaRevision);
        var stored = await fixture.Instances.FindAsync(created.InstanceId, CancellationToken.None);
        Assert.Equal(persona, stored!.Persona);

        var createdEvent = Assert.Single(await ListInstanceEventsAsync(fixture, created.InstanceId));
        Assert.Equal(AdminEventOperationKind.ManagedInstanceCreated, createdEvent.Operation);
        using var summary = JsonDocument.Parse(createdEvent.SummaryJson);
        Assert.Equal("Custom", summary.RootElement.GetProperty("personaSource").GetString());
        Assert.Equal(
            AdminPersonaHistoryFingerprint.Compute(persona),
            summary.RootElement.GetProperty("personaFingerprint").GetString());
    }

    [Fact]
    public async Task CreateManagedAsync_rejects_an_invalid_custom_persona_without_storing_an_instance()
    {
        var fixture = CreateFixture();
        var error = await Assert.ThrowsAsync<AgentCoreException>(() =>
            fixture.Service.CreateManagedAsync(
                "examiner",
                1,
                new AgentIdentity("", "Guide", "A field guide.", "Direct"),
                CancellationToken.None).AsTask());

        Assert.Equal("ValidationError", error.Code);
        Assert.Empty(await fixture.Instances.ListAsync(10, CancellationToken.None));
        Assert.Empty(await fixture.Events.ListAsync(new AdminEventListQuery(Limit: 20), CancellationToken.None));
    }

    [Fact]
    public async Task ListEvents_returns_a_legacy_managed_instance_created_summary()
    {
        var fixture = CreateFixture();
        var instanceId = Guid.Parse("019944af-00d1-7000-8000-0000000000c4");
        var append = AdminEventFactory.ManagedInstanceCreated(
            Guid.Parse("019944af-00d1-7000-8000-0000000000c5"),
            DateTimeOffset.Parse("2026-09-28T00:00:00Z"),
            "examiner",
            instanceId,
            1);
        Assert.DoesNotContain("personaSource", append.SummaryJson, StringComparison.Ordinal);
        await fixture.Events.AppendAsync(append, CancellationToken.None);

        var listed = await new AdminHistoryService(fixture.Events).ListEventsAsync(
            new AdminEventListQuery("agent.instance", instanceId.ToString("D")),
            CancellationToken.None);

        Assert.Equal(append.SummaryJson, Assert.Single(listed).SummaryJson);
    }

    private static async Task<IReadOnlyList<AdminEvent>> ListInstanceEventsAsync(Fixture fixture, Guid instanceId) =>
        await fixture.Events.ListAsync(
            new AdminEventListQuery("agent.instance", instanceId.ToString("D")),
            CancellationToken.None);

    private static AgentIdentity DefinitionPersona() =>
        new("Alex", "Examiner", "Practice.", "Calm");

    private static Fixture CreateFixture()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-28T00:00:00Z"));
        var ids = new SystemIdGenerator(clock);
        var events = new InMemoryAdminEventStore(ids);
        var instances = new InMemoryAgentInstanceStore { EventStore = events };
        var service = new AdminAgentInstanceService(
            new SingleDefinitionStore(SampleDefinition()),
            instances,
            events,
            ids,
            clock);
        return new Fixture(service, instances, events);
    }

    private static AgentDefinition SampleDefinition() =>
        new(
            1,
            "examiner",
            1,
            DefinitionPersona(),
            ["Conduct practice"],
            "You are Alex.",
            new BehaviorPolicy("acknowledgeThenContinue", true, true),
            new ConversationPolicy("concise", true, "en", 256),
            new InitiativePolicy(true, 8000, 30000, 1, ["longSilence"]),
            new VoiceConfiguration(true, "default", 1.0),
            new ProviderPreferences("primary-llm", "primary-stt", "primary-tts"),
            new Dictionary<string, string>());

    private sealed record Fixture(
        AdminAgentInstanceService Service,
        InMemoryAgentInstanceStore Instances,
        InMemoryAdminEventStore Events);

    private sealed class SingleDefinitionStore(AgentDefinition definition) : IAgentDefinitionStore
    {
        public ValueTask<AgentDefinition?> GetAsync(
            string id,
            int? version = null,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                definition.Id == id && (version is null || definition.Version == version) ? definition : null);

        public ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AgentDefinition>>([definition]);
    }
}
