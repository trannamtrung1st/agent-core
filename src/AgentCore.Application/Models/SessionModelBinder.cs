using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Models;

public static class SessionModelBinder
{
    public static bool IsInheritedOffering(string? offeringId) => offeringId is null;

    // The same inherited choice drives public Chat discovery and actual admission.
    // Missing/disabled references remain the chosen ID; discovery must not substitute a model.
    public static (string OfferingId, string? Effort, ModelSelectionSource Source) InheritedChoice(
        IModelCatalog catalog, AgentModelDefaults? defaults)
    {
        var agentChoice = defaults?.OfferingId is not null;
        return (agentChoice ? defaults!.OfferingId!.Trim() : catalog.DefaultOfferingId,
            NormalizeEffort(defaults?.ReasoningEffort) ?? (agentChoice ? null : catalog.DefaultReasoningEffort),
            agentChoice ? ModelSelectionSource.AgentDefault : ModelSelectionSource.TenantDefault);
    }

    public static SessionModelSelection Bind(
        IModelCatalog catalog,
        string? requestedOfferingId,
        string? requestedEffort,
        ModelSelectionSource explicitSource,
        AgentModelDefaults? agentDefaults = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);

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

        return Bind(descriptor, effort, source) with { HasExplicitReasoningEffort = NormalizeEffort(requestedEffort) is not null };
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
            HasExplicitReasoningEffort: NormalizeEffort(requestedEffort) is not null,
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
            return Bind(descriptor, existing.SelectionSource == ModelSelectionSource.Host || existing.HasExplicitReasoningEffort ? existing.ReasoningEffort : null, existing.SelectionSource)
                with { HasExplicitReasoningEffort = existing.HasExplicitReasoningEffort };
        }
        return Bind(catalog, null, existing?.HasExplicitReasoningEffort == true ? existing.ReasoningEffort : null,
            ModelSelectionSource.TenantDefault, definition.ModelDefaults);
    }

    public static string ToWire(ModelSelectionSource source) =>
        source switch
        {
            ModelSelectionSource.TenantDefault => "tenantDefault",
            ModelSelectionSource.AgentDefault => "agentDefault",
            ModelSelectionSource.User => "user",
            ModelSelectionSource.Host => "host",
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
