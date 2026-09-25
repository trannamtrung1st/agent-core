using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tests;

public sealed class AdminReadServiceTests
{
    [Fact]
    public async Task Effective_configuration_uses_exact_active_version_not_latest()
    {
        var definitions = new VersionedDefinitions(
            Sample("examiner", 1, "Examiner v1"),
            Sample("examiner", 2, "Examiner v2"));
        var instance = new AgentInstance(
            Guid.Parse("019944af-00d1-7000-8000-000000000001"),
            "examiner",
            1,
            new AgentIdentity("Pinned", "role", "desc", "tone"),
            AgentInstanceLifecycle.Active,
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            DateTimeOffset.Parse("2026-01-02T00:00:00Z"),
            Compatibility: false);
        var service = new AdminReadService(
            definitions,
            new SingleInstanceStore(instance),
            TestModelCatalogs.Synthetic(),
            new AllowAllToolGate());

        var config = await service.GetEffectiveConfigurationAsync(instance.InstanceId);
        Assert.Equal(1, config.DefinitionVersion);
        Assert.Equal("Pinned", config.Persona.Name);
        Assert.False(string.IsNullOrWhiteSpace(config.EffectiveModel.CatalogKey));
    }

    [Fact]
    public async Task Durable_eligibility_is_false_when_trigger_enabled_but_no_allowed_sources()
    {
        var definition = Sample("demo", 1, "Demo") with
        {
            TriggerPolicy = new TriggerPolicy(
                Enabled: true,
                AllowUserScheduling: true,
                AllowOneShot: true,
                AllowDaily: true,
                AllowWeekly: true,
                AllowIndefiniteRecurrence: true,
                MaxActiveRegistrations: 1,
                OneShotHorizonDays: 1,
                MinRecurrenceDays: 1,
                AllowedSourceKinds: [])
        };
        var definitions = new VersionedDefinitions(definition);
        var instance = new AgentInstance(
            Guid.Parse("019944af-00d1-7000-8000-000000000002"),
            "demo",
            1,
            definition.Identity,
            AgentInstanceLifecycle.Active,
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            DateTimeOffset.Parse("2026-01-02T00:00:00Z"),
            Compatibility: false);
        var service = new AdminReadService(
            definitions,
            new SingleInstanceStore(instance),
            TestModelCatalogs.Synthetic(),
            new AllowAllToolGate());

        var config = await service.GetEffectiveConfigurationAsync(instance.InstanceId);
        Assert.True(config.DurableExecutionEligibility.TriggerPolicyEnabled);
        Assert.False(config.DurableExecutionEligibility.AllowsScheduleSource);
        Assert.False(config.DurableExecutionEligibility.AllowsApplicationEventSource);
        Assert.False(config.DurableExecutionEligibility.CanAcceptNewTriggeredWork);
    }

    private static AgentDefinition Sample(string id, int version, string name) =>
        new(
            1,
            id,
            version,
            new AgentIdentity(name, "role", "desc", "tone"),
            ["goal"],
            "instructions",
            new BehaviorPolicy("polite", true, true),
            new ConversationPolicy("short", true, "en", 512),
            new InitiativePolicy(false, 1_000, 1_000, 1, []),
            new VoiceConfiguration(false, "voice", 1),
            new ProviderPreferences("primary-llm", null, null),
            new Dictionary<string, string>());

    private sealed class AllowAllToolGate : IToolConfigurationGate
    {
        public bool IsConfigured(string toolName) => true;
    }

    private sealed class SingleInstanceStore(AgentInstance instance) : IAgentInstanceStore
    {
        public ValueTask<IReadOnlyList<AgentInstance>> ListAsync(int limit, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AgentInstance>>([instance]);

        public ValueTask<AgentInstance?> FindAsync(Guid instanceId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(instanceId == instance.InstanceId ? instance : null);

        public ValueTask<AgentInstance?> FindCompatibilityAsync(string definitionId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<AgentInstance?>(null);

        public ValueTask InsertAsync(AgentInstance inserted, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask UpdateActiveVersionAsync(
            Guid instanceId,
            int activeVersion,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class VersionedDefinitions(params AgentDefinition[] definitions) : IAgentDefinitionStore
    {
        private readonly Dictionary<(string, int), AgentDefinition> _items = definitions.ToDictionary(
            item => (item.Id, item.Version));

        public ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AgentDefinition>>(_items.Values.ToArray());

        public ValueTask<AgentDefinition?> GetAsync(string id, int? version = null, CancellationToken cancellationToken = default)
        {
            if (version is int exact)
            {
                return ValueTask.FromResult(_items.TryGetValue((id, exact), out var found) ? found : null);
            }

            var latest = _items.Values.Where(item => item.Id == id).MaxBy(item => item.Version);
            return ValueTask.FromResult<AgentDefinition?>(latest);
        }
    }
}
