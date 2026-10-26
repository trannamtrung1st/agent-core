using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Models;

public sealed record ExecutionModelDecision(ExecutionModelPin? Pin, string? FailureCode)
{
    public bool Accepted => Pin is not null && FailureCode is null;
}

public static class ExecutionModelPolicy
{
    public static void RequireCurrentDecision(AgentRunModelPin pin, long settingsRevision)
    {
        if (pin.Decision is { Basis: "ranked" or "singleEligible" or "configuredDefault" or "fixedAutoReasoning" } decision
            && decision.SettingsRevision != settingsRevision) throw new AgentCore.Application.Security.SecurityConflictException("model_source_changed");
    }

    public const string UnavailableCode = "model-unavailable";
    public const string CapabilityCode = "model-capability-unsupported";

    public static void RequireSelectable(IModelCatalog catalog, string? offeringId, string? reasoningEffort)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (offeringId is null)
        {
            if (!string.IsNullOrWhiteSpace(reasoningEffort))
            {
                throw AgentCoreErrors.Validation("Reasoning effort requires a model.");
            }

            return;
        }

        if (!Guid.TryParseExact(offeringId, "D", out var id) || id == Guid.Empty || id.ToString("D") != offeringId)
            throw AgentCoreErrors.Validation("The selected model must be a canonical offering UUID.");

        var descriptor = catalog.Get(offeringId.Trim());
        if (descriptor is null || !descriptor.SupportsInstructionHierarchy || !Enum.IsDefined(descriptor.Transport))
        {
            throw AgentCoreErrors.Validation("The selected model is not available.");
        }

        if (string.IsNullOrWhiteSpace(reasoningEffort))
        {
            return;
        }

        if (!descriptor.Reasoning
            || !descriptor.SupportedReasoningEfforts.Contains(reasoningEffort.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            throw AgentCoreErrors.Validation("The selected reasoning effort is not available for this model.");
        }
    }

    public static AgentCore.Domain.Models.ModelPreference MergePreference(AgentCore.Domain.Models.ModelPreference? lower,
        AgentCore.Domain.Models.ModelPreference? higher) => higher is null ? lower ?? AgentCore.Domain.Models.ModelPreference.Inherit
        : higher.Mode == "inherit" ? (lower ?? AgentCore.Domain.Models.ModelPreference.Inherit) with { Reasoning = higher.Reasoning ?? lower?.Reasoning }
        : higher;

    public static AgentCore.Domain.Models.ModelPreference Preference(AgentDefinition definition, AgentInstance instance, Automation? registration) =>
        MergePreference(MergePreference(definition.ModelDefaults, instance.UnattendedModel), registration?.ModelPreference);

    public static bool RequiresRouting(AgentDefinition definition, AgentInstance instance, Automation? registration)
    {
        var preference = Preference(definition, instance, registration);
        return preference.Mode == "auto" || preference.Reasoning?.Mode == "auto";
    }

    public static bool Selectable(IModelCatalog catalog, AgentDefinition definition, AgentInstance instance, Automation? registration)
    {
        var preference = Preference(definition, instance, registration);
        if (!RequiresRouting(definition, instance, registration)) return Resolve(catalog, definition, instance, registration).Accepted;
        return catalog.RoutingPolicy.Enabled && catalog.Models.Any(m => m.SupportsInstructionHierarchy && Enum.IsDefined(m.Transport) && m.Tools
            && (preference.Mode != "fixed" || m.OfferingId == preference.OfferingId) && (registration?.RequiresVision != true || m.Vision));
    }

    public static void RequireSelectable(IModelCatalog catalog, AgentCore.Domain.Models.ModelPreference? preference)
    {
        preference?.Validate();
        if (preference?.Mode is "auto" or "inherit") return;
        RequireSelectable(catalog, preference?.OfferingId, preference?.Reasoning?.Effort);
    }

    public static async ValueTask<ExecutionModelDecision> ResolveAsync(AutoModelRouter router, IModelCatalog catalog,
        AgentDefinition definition, AgentInstance instance, Automation? registration, CancellationToken ct)
    {
        try
        {
            var preference = Preference(definition, instance, registration);
            var selection = await router.ResolveAsync(catalog, preference, definition.ModelDefaults, registration?.RequiresVision == true, true,
                registration?.ModelPreference?.Mode is "fixed" or "auto" ? "automation" : instance.UnattendedModel?.Mode is "fixed" or "auto" ? "unattended" : "agentDefault",
                new Dictionary<string, string> { ["inputMode"] = "unattended", ["requiresVision"] = (registration?.RequiresVision == true).ToString(),
                    ["inputCharacters"] = (registration?.Instructions.Length ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture) }, ct).ConfigureAwait(false);
            return new(new(selection.OfferingId, selection.ConnectionId, selection.ExternalModelId, selection.ReasoningEffort,
                registration?.ModelPreference?.Mode is "fixed" or "auto" ? ExecutionModelSource.TriggerOverride : instance.UnattendedModel?.Mode is "fixed" or "auto" ? ExecutionModelSource.UnattendedDefault : ExecutionModelSource.ConversationDefault,
                selection.Binding, selection.Decision), null);
        }
        catch (AgentCoreException) { return new(null, UnavailableCode); }
    }

    public static ExecutionModelDecision Resolve(IModelCatalog catalog, AgentDefinition definition, AgentInstance instance, Automation? registration)
    {
        if (RequiresRouting(definition, instance, registration)) return new(null, "model-routing-required");
        var preference = Preference(definition, instance, registration);
        try
        {
            var selection = SessionModelBinder.BindPreference(catalog, preference, definition.ModelDefaults);
            return Admit(catalog, new(selection.OfferingId, selection.ConnectionId, selection.ExternalModelId,
                selection.ReasoningEffort, registration?.ModelPreference?.Mode == "fixed" ? ExecutionModelSource.TriggerOverride
                    : instance.UnattendedModel?.Mode == "fixed" ? ExecutionModelSource.UnattendedDefault : ExecutionModelSource.ConversationDefault, selection.Binding), definition, registration);
        }
        catch (AgentCoreException) { return new(null, UnavailableCode); }
    }

    public static ExecutionModelDecision Validate(
        IModelCatalog catalog,
        ExecutionModelPin pin,
        AgentDefinition definition,
        Automation? registration)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(pin);
        ArgumentNullException.ThrowIfNull(definition);
        if (!Matches(catalog, pin, out _))
        {
            return new ExecutionModelDecision(pin, UnavailableCode);
        }

        return Admit(catalog, pin, definition, registration);
    }

    public static bool Matches(IModelCatalog catalog, ExecutionModelPin pin, out ModelDescriptor? descriptor)
    {
        descriptor = catalog.Get(pin.OfferingId);
        if (descriptor is null || !descriptor.SupportsInstructionHierarchy || !Enum.IsDefined(descriptor.Transport)
            || !string.Equals(descriptor.ConnectionId, pin.ConnectionId, StringComparison.Ordinal)
            || !string.Equals(descriptor.ExternalModelId, pin.ExternalModelId, StringComparison.Ordinal))
        {
            return false;
        }
        if (descriptor.Route != pin.Binding?.Route) return false;

        if (string.IsNullOrWhiteSpace(pin.ReasoningEffort))
        {
            return true;
        }

        return descriptor.Reasoning
            && descriptor.SupportedReasoningEfforts.Contains(pin.ReasoningEffort, StringComparer.OrdinalIgnoreCase);
    }

    private static ExecutionModelDecision Admit(
        IModelCatalog catalog,
        ExecutionModelPin pin,
        AgentDefinition definition,
        Automation? registration)
    {
        var descriptor = catalog.Get(pin.OfferingId);
        if (descriptor is null || !descriptor.SupportsInstructionHierarchy || !Enum.IsDefined(descriptor.Transport))
        {
            return new ExecutionModelDecision(pin, UnavailableCode);
        }

        if (registration?.RequiresVision == true && !descriptor.Vision)
        {
            return new ExecutionModelDecision(pin, CapabilityCode);
        }

        if (!descriptor.Tools)
        {
            return new ExecutionModelDecision(pin, CapabilityCode);
        }

        return new ExecutionModelDecision(pin, null);
    }

    private static bool RequiresBrowserTools(AgentDefinition definition) =>
        definition.ExecutionPolicy?.ToolList.Any(tool => tool.StartsWith("browser.", StringComparison.Ordinal)) == true;
}
