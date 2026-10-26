using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Models;

public static class SessionModelBinder
{
    public static SessionModelSelection BindPreference(IModelCatalog catalog, AgentCore.Domain.Models.ModelPreference preference,
        AgentCore.Domain.Models.ModelPreference? definitionDefault = null)
    {
        preference.Validate(); definitionDefault?.Validate();
        var effective = preference.Mode == "inherit" ? definitionDefault ?? preference : preference;
        var reasoning = preference.Reasoning ?? effective.Reasoning;
        if (effective.Mode == "auto" || reasoning?.Mode == "auto")
            throw new AgentCoreException("ModelRoutingUnavailable", "Auto resolves once at new Run admission.", 409);
        var selected = Bind(catalog, preference.OfferingId, reasoning?.Effort, ModelSelectionSource.User, definitionDefault);
        if (reasoning?.Mode == "modelDefault")
        {
            var descriptor = catalog.Get(selected.OfferingId)!;
            selected = Bind(descriptor, descriptor.DefaultReasoningEffort, selected.SelectionSource);
        }
        return selected;
    }

    public static bool IsInheritedOffering(string? offeringId) => offeringId is null;

    // The same inherited choice drives public Chat discovery and actual admission.
    // Missing/disabled references remain the chosen ID; discovery must not substitute a model.
    public static (string OfferingId, string? Effort, ModelSelectionSource Source) InheritedChoice(
        IModelCatalog catalog, AgentCore.Domain.Models.ModelPreference? defaults)
    {
        if (defaults?.Mode == "auto") return ("", null, ModelSelectionSource.Auto);
        var agentChoice = defaults?.OfferingId is not null;
        return (agentChoice ? defaults!.OfferingId!.Trim() : catalog.DefaultOfferingId,
            NormalizeEffort(defaults?.Reasoning?.Effort) ?? (agentChoice ? null : catalog.DefaultReasoningEffort),
            agentChoice ? ModelSelectionSource.AgentDefault : ModelSelectionSource.TenantDefault);
    }

    public static SessionModelSelection Bind(
        IModelCatalog catalog,
        string? requestedOfferingId,
        string? requestedEffort,
        ModelSelectionSource explicitSource,
        AgentCore.Domain.Models.ModelPreference? agentDefaults = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        if (requestedOfferingId is null && agentDefaults?.Mode == "auto")
            throw new AgentCoreException("ModelRoutingUnavailable", "Auto resolves once at new Run admission; no deterministic effective model exists yet.", 409);
        var hasExplicitModel = !IsInheritedOffering(requestedOfferingId);
        string key;
        ModelSelectionSource source;
        if (hasExplicitModel)
        {
            key = requestedOfferingId!.Trim();
            source = explicitSource;
        }
        else
        {
            var inherited = InheritedChoice(catalog, agentDefaults);
            key = inherited.OfferingId;
            source = inherited.Source;
        }

        var descriptor = catalog.Get(key)
            ?? (hasExplicitModel ? throw AgentCoreErrors.Validation($"Model '{key}' is not in the catalog.")
                : throw new AgentCoreException("ModelUnavailable", "Configure an enabled model connection and select an available default or an explicit model before starting new work.", 409));

        var effort = NormalizeEffort(requestedEffort);
        if (effort is null && !hasExplicitModel)
            effort = InheritedChoice(catalog, agentDefaults).Effort;

        return Bind(descriptor, effort, source);
    }

    public static SessionModelSelection Bind(
        ModelDescriptor descriptor,
        string? requestedEffort,
        ModelSelectionSource source)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!descriptor.SupportsInstructionHierarchy || !Enum.IsDefined(descriptor.Transport))
            throw AgentCoreErrors.Validation("Selected model/transport does not support the required trusted Core and Runtime instruction hierarchy.");
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
                        ? $"Model '{descriptor.OfferingId}' requires a reasoning effort."
                        : $"Model '{descriptor.OfferingId}' does not support reasoning effort '{effort}'.");
            }

            effort = descriptor.SupportedReasoningEfforts.First(
                value => string.Equals(value, effort, StringComparison.OrdinalIgnoreCase));
        }
        else if (effort is not null)
        {
            throw AgentCoreErrors.Validation($"Model '{descriptor.OfferingId}' does not support reasoning effort.");
        }

        return new SessionModelSelection(
            descriptor.OfferingId,
            descriptor.ConnectionId,
            descriptor.ExternalModelId,
            source,
            effort,
            Binding: descriptor.Route is { } route && descriptor.CapabilityProfile is { } profile
                ? new(route, profile, descriptor.DisplayName, descriptor.ConnectionLabel!) : null);
    }

    public static SessionModelSelection PinDefault(IModelCatalog catalog, AgentDefinition? definition = null) =>
        Bind(catalog, requestedOfferingId: null, requestedEffort: null, ModelSelectionSource.TenantDefault, definition?.ModelDefaults);

    public static SessionModelSelection RefreshDefault(IModelCatalog catalog, AgentDefinition definition, SessionModelSelection? existing)
    {
        if (existing?.SelectionSource is ModelSelectionSource.User or ModelSelectionSource.Host)
        {
            var descriptor = catalog.Get(existing.OfferingId)
                ?? throw new AgentCoreException("ModelUnavailable", "The selected model is unavailable. Select an enabled offering for a new Run.", 409);
            if (existing.SelectionSource == ModelSelectionSource.Host && descriptor.Route != existing.Binding?.Route)
                throw new AgentCoreException("ModelRouteStale", "The admitted model route changed. Start new work with a current model.", 409);
            return Bind(descriptor, existing.ReasoningEffort, existing.SelectionSource);
        }
        return Bind(catalog, null, null,
            ModelSelectionSource.TenantDefault, definition.ModelDefaults);
    }

    public static string ToWire(ModelSelectionSource source) =>
        source switch
        {
            ModelSelectionSource.TenantDefault => "tenantDefault",
            ModelSelectionSource.AgentDefault => "agentDefault",
            ModelSelectionSource.User => "user",
            ModelSelectionSource.Host => "host",
            ModelSelectionSource.Auto => "auto",
            _ => throw new ArgumentOutOfRangeException(nameof(source))
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
