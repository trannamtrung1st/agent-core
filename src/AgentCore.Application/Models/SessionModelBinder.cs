using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Models;

public static class SessionModelBinder
{
    public const string DefaultKey = "default";

    public static bool IsDefaultKey(string? key) =>
        string.IsNullOrWhiteSpace(key)
        || string.Equals(key.Trim(), DefaultKey, StringComparison.OrdinalIgnoreCase);

    public static SessionModelSelection Bind(
        IModelCatalog catalog,
        string? requestedKey,
        string? requestedEffort,
        ModelSelectionSource explicitSource,
        AgentModelDefaults? agentDefaults = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var hasExplicitModel = !IsDefaultKey(requestedKey);
        string key;
        ModelSelectionSource source;
        if (hasExplicitModel)
        {
            key = requestedKey!.Trim();
            source = explicitSource;
        }
        else if (!IsDefaultKey(agentDefaults?.CatalogKey))
        {
            key = agentDefaults!.CatalogKey!.Trim();
            source = ModelSelectionSource.AgentDefault;
        }
        else
        {
            key = catalog.DefaultKey;
            source = ModelSelectionSource.SystemDefault;
        }

        var descriptor = catalog.Get(key)
            ?? throw AgentCoreErrors.Validation($"Model '{key}' is not in the catalog.");

        var effort = NormalizeEffort(requestedEffort);
        if (effort is null && !hasExplicitModel)
        {
            effort = NormalizeEffort(agentDefaults?.ReasoningEffort);
        }

        return Bind(descriptor, effort, source);
    }

    public static SessionModelSelection Bind(
        ModelDescriptor descriptor,
        string? requestedEffort,
        ModelSelectionSource source)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var effort = NormalizeEffort(requestedEffort);
        if (descriptor.Reasoning)
        {
            if (effort is null)
            {
                effort = descriptor.DefaultReasoningEffort;
            }

            if (effort is null
                || !descriptor.SupportedReasoningEfforts.Contains(effort, StringComparer.OrdinalIgnoreCase))
            {
                throw AgentCoreErrors.Validation(
                    effort is null
                        ? $"Model '{descriptor.Key}' requires a reasoning effort."
                        : $"Model '{descriptor.Key}' does not support reasoning effort '{effort}'.");
            }

            effort = descriptor.SupportedReasoningEfforts.First(
                value => string.Equals(value, effort, StringComparison.OrdinalIgnoreCase));
        }
        else if (effort is not null)
        {
            throw AgentCoreErrors.Validation($"Model '{descriptor.Key}' does not support reasoning effort.");
        }

        return new SessionModelSelection(
            descriptor.Key,
            descriptor.ProviderAlias,
            descriptor.ModelId,
            source,
            effort);
    }

    public static SessionModelSelection PinDefault(IModelCatalog catalog, AgentDefinition? definition = null) =>
        Bind(catalog, requestedKey: null, requestedEffort: null, ModelSelectionSource.SystemDefault, definition?.ModelDefaults);

    public static string ToWire(ModelSelectionSource source) =>
        source switch
        {
            ModelSelectionSource.SystemDefault => "systemDefault",
            ModelSelectionSource.AgentDefault => "agentDefault",
            ModelSelectionSource.User => "user",
            ModelSelectionSource.Host => "host",
            _ => "systemDefault"
        };

    private static string? NormalizeEffort(string? effort)
    {
        if (string.IsNullOrWhiteSpace(effort))
        {
            return null;
        }

        return effort.Trim();
    }
}
