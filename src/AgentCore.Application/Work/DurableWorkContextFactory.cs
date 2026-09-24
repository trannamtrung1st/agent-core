using AgentCore.Application.Memory;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Work;

namespace AgentCore.Application.Work;

public sealed class DurableWorkContextFactory(
    IAgentInstanceStore instances,
    IAgentDefinitionStore definitions,
    IMemoryStore memory,
    IStructuredMemoryService memories,
    IModelCatalog catalog,
    ILanguageModelResolver models,
    TimeProvider time)
{
    public async ValueTask<AgentContext> CreateAsync(WorkItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Provenance.SourceKind != WorkSourceKind.Schedule)
        {
            throw AgentCoreErrors.Validation("Only a scheduled reminder can use this context.");
        }

        var instance = await instances.FindAsync(item.Owner.AgentInstanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null || instance.Lifecycle != AgentInstanceLifecycle.Active)
        {
            throw AgentCoreErrors.NotFound("Agent instance was not found.");
        }

        if (!string.Equals(instance.DefinitionId, item.Provenance.DefinitionId, StringComparison.Ordinal)
            || instance.ActiveVersion != item.Provenance.DefinitionVersion
            || !string.Equals(instance.Persona.Name, item.Provenance.PersonaName, StringComparison.Ordinal))
        {
            throw AgentCoreErrors.Validation("Pinned agent identity is no longer eligible.");
        }

        var definition = await definitions.GetAsync(
            item.Provenance.DefinitionId,
            item.Provenance.DefinitionVersion,
            cancellationToken).ConfigureAwait(false);
        if (definition is null)
        {
            throw AgentCoreErrors.Validation("Pinned agent identity is no longer eligible.");
        }

        var profile = await memory.LoadProfileAsync(item.Owner.ProfileId, cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            throw AgentCoreErrors.NotFound("Trusted profile was not found.");
        }

        var descriptor = catalog.Get(item.Model.CatalogKey);
        if (descriptor is null
            || !string.Equals(descriptor.ProviderAlias, item.Model.ProviderAlias, StringComparison.Ordinal)
            || !string.Equals(descriptor.ModelId, item.Model.ModelId, StringComparison.Ordinal)
            || !EffortAllowed(descriptor, item.Model.ReasoningEffort))
        {
            throw AgentCoreErrors.Validation("Pinned model is unavailable.");
        }

        var selection = new SessionModelSelection(
            descriptor.Key,
            descriptor.ProviderAlias,
            descriptor.ModelId,
            ModelSelectionSource.SystemDefault,
            item.Model.ReasoningEffort);
        var learned = await SessionMemoryPrompt.LoadOwnerAsync(
            memories,
            instance.InstanceId,
            definition,
            profile,
            cancellationToken).ConfigureAwait(false);
        return new AgentContext(
            definition,
            [],
            string.Empty,
            profile,
            SessionMode.Text,
            null,
            false,
            null,
            new AgentTrigger(item.Provenance.SourceOccurrenceId, TriggerKind.ScheduledOccurrence, item.Provenance.EvidenceJson),
            UtcNow: time.GetUtcNow(),
            LanguageModel: models.Resolve(selection, ModelPurpose.Conversation),
            ReasoningEffort: item.Model.ReasoningEffort,
            LearnedMemories: learned,
            Persona: instance.Persona,
            ModelSupportsTools: descriptor.Tools);
    }

    private static bool EffortAllowed(ModelDescriptor descriptor, string? effort)
    {
        if (string.IsNullOrWhiteSpace(effort))
        {
            return true;
        }

        return descriptor.Reasoning
            && descriptor.SupportedReasoningEfforts.Contains(effort, StringComparer.OrdinalIgnoreCase);
    }
}
