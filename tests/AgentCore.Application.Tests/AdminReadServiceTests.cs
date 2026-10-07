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
            DateTimeOffset.Parse("2026-01-02T00:00:00Z"));
        var service = new AdminReadService(
            definitions,
            definitions,
            new EmptyAdminDefinitionStore(),
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
            DateTimeOffset.Parse("2026-01-02T00:00:00Z"));
        var service = new AdminReadService(
            definitions,
            definitions,
            new EmptyAdminDefinitionStore(),
            new SingleInstanceStore(instance),
            TestModelCatalogs.Synthetic(),
            new AllowAllToolGate());

        var config = await service.GetEffectiveConfigurationAsync(instance.InstanceId);
        Assert.True(config.DurableExecutionEligibility.TriggerPolicyEnabled);
        Assert.False(config.DurableExecutionEligibility.AllowsScheduleSource);
        Assert.False(config.DurableExecutionEligibility.AllowsApplicationEventSource);
        Assert.False(config.DurableExecutionEligibility.CanAcceptNewTriggeredWork);
    }

    [Fact]
    public async Task Effective_configuration_projects_shared_definition_fields()
    {
        var preferences = new ProviderPreferences("primary-llm", "primary-stt", "primary-tts");
        var definition = Sample("examiner", 1, "Examiner v1") with
        {
            ProviderPreferences = preferences,
            Environment = new RoleEnvironment(
                Harness: ["beta-harness", "alpha-harness"],
                KnowledgeSources: [new KnowledgeSourceRef("handbook", "Handbook", "cite-handbook")],
                ToolAllowlist: [ToolCatalog.KnowledgeRetrieve, ToolCatalog.WorkspaceCopy, ToolCatalog.WorkspaceRead],
                Workspace: new WorkspaceTemplatePolicy("support-desk"))
        };
        var instance = new AgentInstance(
            Guid.Parse("019944af-00d1-7000-8000-000000000003"),
            "examiner",
            1,
            definition.Identity,
            AgentInstanceLifecycle.Active,
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            DateTimeOffset.Parse("2026-01-02T00:00:00Z"));
        var service = new AdminReadService(
            new VersionedDefinitions(definition),
            new VersionedDefinitions(definition),
            new EmptyAdminDefinitionStore(),
            new SingleInstanceStore(instance),
            TestModelCatalogs.Synthetic(),
            new AllowAllToolGate());

        var config = await service.GetEffectiveConfigurationAsync(instance.InstanceId);

        Assert.Equal(["alpha-harness", "beta-harness"], config.HarnessReferences);
        Assert.Equal("support-desk", config.WorkspaceTemplateId);
        var source = Assert.Single(config.KnowledgeSources);
        Assert.Equal("handbook", source.Identity);
        Assert.Equal("Handbook", source.Title);
        Assert.Equal("cite-handbook", source.Citation);
        Assert.Equal("knowledge/handbook", source.ResolvedResourcePath);
        Assert.Equal(MemoryPolicy.Disabled, config.MemoryPolicy);
        Assert.Equal(preferences, config.ProviderPreferences);
        Assert.Equal(new[] { ToolCatalog.KnowledgeRetrieve, ToolCatalog.WorkspaceCopy, ToolCatalog.WorkspaceRead }, config.EffectiveToolAllowlist);
        Assert.Equal("scripted-alpha", config.EffectiveModel.CatalogKey);
        Assert.Equal("systemDefault", config.EffectiveModel.SelectionSource);
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

        public ValueTask InsertAsync(AgentInstance inserted, CancellationToken cancellationToken = default, IReadOnlyList<AgentCore.Domain.Definitions.SkillSpec>? initialSkills = null) =>
            ValueTask.CompletedTask;

        public ValueTask UpdateActiveVersionAsync(
            Guid instanceId,
            int activeVersion,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask<AgentInstance> UpdateWithExpectedRevisionAsync(
            AgentInstanceRevisionUpdate update,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(instance);

        public ValueTask<AgentInstance> InsertManagedWithHistoryAsync(
            AgentInstance inserted,
            AdminEventAppend historyAppend,
            CancellationToken cancellationToken = default, IReadOnlyList<AgentCore.Domain.Definitions.SkillSpec>? initialSkills = null) =>
            ValueTask.FromResult(instance);

        public ValueTask<AgentInstance> UpdateActiveVersionWithHistoryAsync(
            AgentInstanceRevisionUpdate update,
            DateTimeOffset updatedAt,
            AdminEventAppend historyAppend,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(instance);

        public ValueTask<AgentInstance> UpdatePersonaWithHistoryAsync(
            AgentInstanceRevisionUpdate update,
            DateTimeOffset updatedAt,
            AdminEventAppend historyAppend,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(instance);

        public ValueTask<AgentInstance> UpdateLifecycleWithHistoryAsync(
            AgentInstanceRevisionUpdate update,
            DateTimeOffset updatedAt,
            AdminEventAppend historyAppend,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(instance);
    }

    private sealed class EmptyAdminDefinitionStore : IAgentDefinitionAdminStore
    {
        public ValueTask<AgentDefinitionDraft> CreateDraftAsync(
            AgentDefinitionDraftCreate create,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<AgentDefinitionDraft> UpdateDraftAsync(
            AgentDefinitionDraftUpdate update,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DeleteDraftAsync(
            AgentDefinitionDraftDelete delete,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<AgentDefinitionDraft> BumpDraftRevisionAsync(
            AgentDefinitionDraftRevisionBump bump,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<AgentDefinitionPublication> PublishDraftAsync(
            AgentDefinitionDraftPublish publish,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<AgentDefinitionDraft?> GetDraftAsync(Guid draftId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<AgentDefinitionDraft?>(null);

        public ValueTask<IReadOnlyList<AgentDefinitionDraftSummary>> ListDraftsAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AgentDefinitionDraftSummary>>([]);

        public ValueTask<AgentDefinitionPublication?> GetPublicationAsync(
            string definitionId,
            int version,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<AgentDefinitionPublication?>(null);

        public ValueTask<IReadOnlyList<AgentDefinitionPublicationSummary>> ListPublicationsAsync(
            string? definitionId = null,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AgentDefinitionPublicationSummary>>([]);

        public ValueTask<AgentDefinitionPublication> DeprecatePublicationAsync(
            AgentDefinitionPublicationDeprecate deprecate,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class VersionedDefinitions(params AgentDefinition[] definitions)
        : IAgentDefinitionStore, IBuiltInAgentDefinitionStore
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
