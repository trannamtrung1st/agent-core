using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Admin;

public sealed class AdminReadService(
    IAgentDefinitionStore definitions,
    IAgentInstanceStore instances,
    IModelCatalog catalog,
    IToolConfigurationGate configurationGate)
{
    public const int MaxInventoryItems = 256;

    public async ValueTask<IReadOnlyList<AdminDefinitionInventoryItem>> ListDefinitionsAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await definitions.ListAsync(cancellationToken).ConfigureAwait(false);
        return rows
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .ThenBy(item => item.Version)
            .Take(MaxInventoryItems)
            .Select(MapDefinition)
            .ToArray();
    }

    public async ValueTask<IReadOnlyList<AdminInstanceInventoryItem>> ListInstancesAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await instances.ListAsync(MaxInventoryItems, cancellationToken).ConfigureAwait(false);
        return rows
            .OrderBy(item => item.DefinitionId, StringComparer.Ordinal)
            .ThenBy(item => item.InstanceId)
            .Select(MapInstance)
            .ToArray();
    }

    public async ValueTask<AdminEffectiveConfiguration> GetEffectiveConfigurationAsync(
        Guid instanceId,
        CancellationToken cancellationToken = default)
    {
        var instance = await instances.FindAsync(instanceId, cancellationToken).ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Agent instance was not found.");

        var definition = await definitions
            .GetAsync(instance.DefinitionId, instance.ActiveVersion, cancellationToken)
            .ConfigureAwait(false);
        if (definition is null)
        {
            throw AgentCoreErrors.NotFound(
                $"Agent '{instance.DefinitionId}' version {instance.ActiveVersion} was not found.");
        }

        return AdminEffectiveConfigurationResolver.Resolve(instance, definition, catalog, configurationGate);
    }

    private static AdminDefinitionInventoryItem MapDefinition(AgentDefinition definition) =>
        new(
            definition.Id,
            definition.Version,
            AdminDefinitionSources.BuiltIn,
            AdminDefinitionStatuses.Published,
            definition.Identity.Name);

    private static AdminInstanceInventoryItem MapInstance(AgentInstance instance) =>
        new(
            instance.InstanceId,
            instance.DefinitionId,
            instance.ActiveVersion,
            instance.Lifecycle,
            instance.Compatibility,
            instance.Persona.Name,
            instance.CreatedAt,
            instance.UpdatedAt);
}

public static class AdminDefinitionSources
{
    public const string BuiltIn = "builtIn";
}

public static class AdminDefinitionStatuses
{
    public const string Published = "published";
}

public sealed record AdminDefinitionInventoryItem(
    string DefinitionId,
    int Version,
    string Source,
    string Status,
    string DisplayName);

public sealed record AdminInstanceInventoryItem(
    Guid InstanceId,
    string DefinitionId,
    int ActiveVersion,
    AgentInstanceLifecycle Lifecycle,
    bool Compatibility,
    string PersonaName,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record AdminKnowledgeSource(string Identity, string Title, string Citation);

public sealed record AdminEffectiveModel(
    string CatalogKey,
    string DisplayName,
    string SelectionSource,
    string? ReasoningEffort,
    string? ModelId);

public sealed record AdminDurableExecutionEligibility(
    bool InstanceActive,
    bool DefinitionResolved,
    bool TriggerPolicyEnabled,
    bool AllowsScheduleSource,
    bool AllowsApplicationEventSource,
    bool CanAcceptNewTriggeredWork);

public sealed record AdminEffectiveConfiguration(
    string DefinitionSource,
    string DefinitionId,
    int DefinitionVersion,
    string DefinitionStatus,
    Guid InstanceId,
    string InstanceLifecycle,
    bool Compatibility,
    AgentIdentity Persona,
    ProviderPreferences ProviderPreferences,
    AdminEffectiveModel EffectiveModel,
    IReadOnlyList<string> EffectiveToolAllowlist,
    IReadOnlyList<string> HarnessReferences,
    string? WorkspaceTemplateId,
    IReadOnlyList<AdminKnowledgeSource> KnowledgeSources,
    MemoryPolicy MemoryPolicy,
    TriggerPolicy? TriggerPolicy,
    AdminDurableExecutionEligibility DurableExecutionEligibility);
