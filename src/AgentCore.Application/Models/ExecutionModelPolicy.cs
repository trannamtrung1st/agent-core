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

    public static void RequireSelectable(IModelCatalog catalog, string? catalogKey, string? reasoningEffort)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (string.IsNullOrWhiteSpace(catalogKey))
        {
            if (!string.IsNullOrWhiteSpace(reasoningEffort))
            {
                throw AgentCoreErrors.Validation("Reasoning effort requires a model.");
            }

            return;
        }

        var descriptor = catalog.Get(catalogKey.Trim());
        if (descriptor is null)
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
        TriggerRegistration? registration)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(instance);

        if (!string.IsNullOrWhiteSpace(registration?.ModelOverrideCatalogKey))
        {
            return Bind(
                catalog,
                registration.ModelOverrideCatalogKey,
                registration.ModelOverrideReasoningEffort,
                ExecutionModelSource.TriggerOverride,
                definition,
                registration);
        }

        if (!string.IsNullOrWhiteSpace(instance.UnattendedModelCatalogKey))
        {
            return Bind(
                catalog,
                instance.UnattendedModelCatalogKey,
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
                    selection.CatalogKey,
                    selection.ProviderAlias,
                    selection.ModelId,
                    selection.ReasoningEffort,
                    ExecutionModelSource.ConversationDefault),
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
        TriggerRegistration? registration)
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
        descriptor = catalog.Get(pin.CatalogKey);
        if (descriptor is null
            || !string.Equals(descriptor.ProviderAlias, pin.ProviderAlias, StringComparison.Ordinal)
            || !string.Equals(descriptor.ModelId, pin.ModelId, StringComparison.Ordinal))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(pin.ReasoningEffort))
        {
            return true;
        }

        return descriptor.Reasoning
            && descriptor.SupportedReasoningEfforts.Contains(pin.ReasoningEffort, StringComparer.OrdinalIgnoreCase);
    }

    private static ExecutionModelDecision Bind(
        IModelCatalog catalog,
        string catalogKey,
        string? effort,
        ExecutionModelSource source,
        AgentDefinition definition,
        TriggerRegistration? registration)
    {
        var descriptor = catalog.Get(catalogKey);
        if (descriptor is null)
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
                selection.CatalogKey,
                selection.ProviderAlias,
                selection.ModelId,
                selection.ReasoningEffort,
                source),
            definition,
            registration);
    }

    private static ExecutionModelDecision Admit(
        IModelCatalog catalog,
        ExecutionModelPin pin,
        AgentDefinition definition,
        TriggerRegistration? registration)
    {
        var descriptor = catalog.Get(pin.CatalogKey);
        if (descriptor is null)
        {
            return new ExecutionModelDecision(pin, UnavailableCode);
        }

        if (registration?.RequiresVision == true && !descriptor.Vision)
        {
            return new ExecutionModelDecision(pin, CapabilityCode);
        }

        if ((RequiresBrowserTools(definition) || registration?.Provenance.AuthorizationOrigin == TriggerAuthorizationOrigin.AdminThought) && !descriptor.Tools)
        {
            return new ExecutionModelDecision(pin, CapabilityCode);
        }

        return new ExecutionModelDecision(pin, null);
    }

    private static bool RequiresBrowserTools(AgentDefinition definition) =>
        definition.Environment?.ToolList.Any(tool => tool.StartsWith("browser.", StringComparison.Ordinal)) == true;
}

public static class ExecutionModelAdmission
{
    public static async ValueTask<ExecutionModelDecision?> ResolveAsync(
        IModelCatalog? catalog,
        IAgentInstanceStore? instances,
        IAgentDefinitionStore? definitions,
        ITriggerStore store,
        TriggerOwner owner,
        Guid? registrationId,
        CancellationToken cancellationToken)
    {
        if (catalog is null || instances is null || definitions is null)
        {
            return null;
        }

        var instance = await instances.FindAsync(owner.AgentInstanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null || instance.Lifecycle != AgentInstanceLifecycle.Active)
        {
            return new ExecutionModelDecision(null, ExecutionModelPolicy.UnavailableCode);
        }

        var definition = await definitions.GetAsync(instance.DefinitionId, instance.ActiveVersion, cancellationToken)
            .ConfigureAwait(false);
        if (definition is null)
        {
            return new ExecutionModelDecision(null, ExecutionModelPolicy.UnavailableCode);
        }

        TriggerRegistration? registration = null;
        if (registrationId is Guid id)
        {
            registration = await store.GetAsync(owner, id, cancellationToken).ConfigureAwait(false);
        }

        return ExecutionModelPolicy.Resolve(catalog, definition, instance, registration);
    }

    public static async ValueTask<ExecutionModelDecision?> ValidateAsync(
        IModelCatalog? catalog,
        IAgentInstanceStore? instances,
        IAgentDefinitionStore? definitions,
        ITriggerStore store,
        TriggerOccurrence occurrence,
        CancellationToken cancellationToken)
    {
        if (catalog is null || instances is null || definitions is null || occurrence.ModelPin is null)
        {
            return null;
        }

        var instance = await instances.FindAsync(occurrence.Owner.AgentInstanceId, cancellationToken).ConfigureAwait(false);
        var definition = instance is null
            ? null
            : await definitions.GetAsync(instance.DefinitionId, instance.ActiveVersion, cancellationToken).ConfigureAwait(false);
        if (instance is null || definition is null)
        {
            return new ExecutionModelDecision(occurrence.ModelPin, ExecutionModelPolicy.UnavailableCode);
        }

        TriggerRegistration? registration = null;
        if (occurrence.RegistrationId is Guid registrationId)
        {
            registration = await store.GetAsync(occurrence.Owner, registrationId, cancellationToken).ConfigureAwait(false);
        }

        return ExecutionModelPolicy.Validate(catalog, occurrence.ModelPin, definition, occurrence.SourceKind == TriggerSourceKind.ThoughtActivation ? null : registration);
    }
}
