using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
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
        var store = new SingleInstanceStore(instance);
        var service = new AdminReadService(definitions, store);

        var config = await service.GetEffectiveConfigurationAsync(instance.InstanceId);
        Assert.Equal(1, config.DefinitionVersion);
        Assert.Equal("Pinned", config.Persona.Name);
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
