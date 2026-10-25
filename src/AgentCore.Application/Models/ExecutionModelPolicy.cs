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

    public static ExecutionModelDecision Resolve(
        IModelCatalog catalog,
        AgentDefinition definition,
        AgentInstance instance,
        Automation? registration)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(instance);

        if (!string.IsNullOrWhiteSpace(registration?.ModelOverrideOfferingId))
        {
            return Bind(
                catalog,
                registration.ModelOverrideOfferingId,
                registration.ModelOverrideReasoningEffort,
                ExecutionModelSource.TriggerOverride,
                definition,
                registration);
        }

        if (!string.IsNullOrWhiteSpace(instance.UnattendedOfferingId))
        {
            return Bind(
                catalog,
                instance.UnattendedOfferingId,
                instance.UnattendedReasoningEffort,
                ExecutionModelSource.UnattendedDefault,
                definition,
                registration);
        }

        try
        {
            var selection = SessionModelBinder.PinDefault(catalog, definition);
            return Admit(
                catalog,
                new ExecutionModelPin(
                    selection.OfferingId,
                    selection.ConnectionId,
                    selection.ExternalModelId,
                    selection.ReasoningEffort,
                    ExecutionModelSource.ConversationDefault, selection.Binding),
                definition,
                registration);
        }
        catch (AgentCoreException)
        {
            return new ExecutionModelDecision(null, UnavailableCode);
        }
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

    private static ExecutionModelDecision Bind(
        IModelCatalog catalog,
        string offeringId,
        string? effort,
        ExecutionModelSource source,
        AgentDefinition definition,
        Automation? registration)
    {
        var descriptor = catalog.Get(offeringId);
        if (descriptor is null || !descriptor.SupportsInstructionHierarchy || !Enum.IsDefined(descriptor.Transport))
        {
            return new ExecutionModelDecision(null, UnavailableCode);
        }

        SessionModelSelection selection;
        try
        {
            selection = SessionModelBinder.Bind(descriptor, effort, ModelSelectionSource.Host);
        }
        catch (AgentCoreException)
        {
            return new ExecutionModelDecision(null, UnavailableCode);
        }

        return Admit(
            catalog,
            new ExecutionModelPin(
                selection.OfferingId,
                selection.ConnectionId,
                selection.ExternalModelId,
                selection.ReasoningEffort,
                source, selection.Binding),
            definition,
            registration);
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
        definition.Environment?.ToolList.Any(tool => tool.StartsWith("browser.", StringComparison.Ordinal)) == true;
}
