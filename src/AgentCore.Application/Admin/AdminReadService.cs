using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Admin;

public sealed class AdminReadService(IAgentDefinitionStore definitions, IAgentInstanceStore instances)
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

        return BuildEffectiveConfiguration(instance, definition);
    }

    internal static AdminEffectiveConfiguration BuildEffectiveConfiguration(
        AgentInstance instance,
        AgentDefinition definition)
    {
        var environment = RoleEnvironments.Of(definition);
        var trigger = definition.TriggerPolicy;
        var memory = definition.MemoryPolicy ?? MemoryPolicy.Disabled;
        var instanceActive = instance.Lifecycle == AgentInstanceLifecycle.Active;
        var triggerEnabled = trigger?.Enabled == true;
        return new AdminEffectiveConfiguration(
            DefinitionSource: AdminDefinitionSources.BuiltIn,
            DefinitionId: definition.Id,
            DefinitionVersion: definition.Version,
            DefinitionStatus: AdminDefinitionStatuses.Published,
            InstanceId: instance.InstanceId,
            InstanceLifecycle: instance.Lifecycle.ToString(),
            Compatibility: instance.Compatibility,
            Persona: instance.Persona,
            ProviderPreferences: definition.ProviderPreferences,
            ModelDefaults: definition.ModelDefaults,
            EffectiveToolAllowlist: environment.ToolList.OrderBy(item => item, StringComparer.Ordinal).ToArray(),
            KnowledgeSources: environment.KnowledgeList
                .Select(item => new AdminKnowledgeSource(item.Identity, item.Title, item.Citation))
                .ToArray(),
            MemoryPolicy: memory,
            TriggerPolicy: trigger,
            DurableExecutionEligibility: new AdminDurableExecutionEligibility(
                InstanceActive: instanceActive,
                DefinitionResolved: true,
                TriggerPolicyEnabled: triggerEnabled,
                CanAcceptNewTriggeredWork: instanceActive && triggerEnabled));
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

public sealed record AdminDurableExecutionEligibility(
    bool InstanceActive,
    bool DefinitionResolved,
    bool TriggerPolicyEnabled,
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
    AgentModelDefaults? ModelDefaults,
    IReadOnlyList<string> EffectiveToolAllowlist,
    IReadOnlyList<AdminKnowledgeSource> KnowledgeSources,
    MemoryPolicy MemoryPolicy,
    TriggerPolicy? TriggerPolicy,
    AdminDurableExecutionEligibility DurableExecutionEligibility);
