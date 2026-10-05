using AgentCore.Application.Connections;
using AgentCore.Application.Memory;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Triggers;
using AgentCore.Domain.Work;

namespace AgentCore.Application.Work;

public sealed class DurableWorkContextFactory(
    IAgentInstanceStore instances,
    IAgentDefinitionStore definitions,
    IMemoryStore memory,
    IStructuredMemoryService memories,
    IModelCatalog catalog,
    ILanguageModelResolver models,
    TimeProvider time,
    IApplicationConnectionStore? connections = null,
    SessionToolExecutor? tools = null)
{
    public async ValueTask<AgentContext> CreateAsync(WorkItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Provenance.SourceKind is not (WorkSourceKind.Schedule or WorkSourceKind.ApplicationEvent or WorkSourceKind.ThoughtActivation))
        {
            throw AgentCoreErrors.Validation("Only scheduled reminders and application events can use this context.");
        }

        var instance = await instances.FindAsync(item.Owner.AgentInstanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null
            || instance.Lifecycle is not (AgentInstanceLifecycle.Active or AgentInstanceLifecycle.Archived))
        {
            throw AgentCoreErrors.NotFound("Agent instance was not found.");
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

        var storedPin = new ExecutionModelPin(
            item.Model.CatalogKey,
            item.Model.ProviderAlias,
            item.Model.ModelId,
            item.Model.ReasoningEffort,
            ExecutionModelSource.ConversationDefault);
        if (!ExecutionModelPolicy.Matches(catalog, storedPin, out var descriptor) || descriptor is null)
        {
            throw AgentCoreErrors.Validation("Pinned model is unavailable.");
        }

        var selection = new SessionModelSelection(
            descriptor.Key,
            descriptor.ProviderAlias,
            descriptor.ModelId,
            ModelSelectionSource.SystemDefault,
            item.Model.ReasoningEffort);
        string? applicationConnectionStatus = null;
        var trustedConnection = false;
        if (connections is not null)
        {
            var applicationConnection = await connections
                .GetByAgentAsync(item.Owner.AgentInstanceId, cancellationToken)
                .ConfigureAwait(false);
            trustedConnection = applicationConnection?.Status == Domain.Connections.ApplicationConnectionStatus.Connected;
            var formatted = ApplicationConnectionPrompt.Format(applicationConnection);
            applicationConnectionStatus = formatted.Length == 0 ? null : formatted;
        }

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
            new AgentTrigger(item.Provenance.SourceOccurrenceId, SourceTrigger(item), item.Provenance.EvidenceJson),
            UtcNow: time.GetUtcNow(),
            LanguageModel: models.Resolve(selection, ModelPurpose.Conversation),
            ReasoningEffort: item.Model.ReasoningEffort,
            LearnedMemories: learned,
            Persona: item.Provenance.ResolvePersona(definition),
            ModelSupportsTools: descriptor.Tools,
            ModelSupportsVision: descriptor.Vision,
            DetachedExecution: true,
            ApplicationConnectionStatus: applicationConnectionStatus,
            TrustedConnection: trustedConnection,
            Harness: item.Provenance.SourceKind == WorkSourceKind.ThoughtActivation && tools is not null
                ? await tools.HarnessContextAsync(instance.InstanceId, cancellationToken) : null,
            ContinuityContext: tools is not null ? await tools.ContinuityContextAsync(instance.InstanceId, item.Provenance.EvidenceJson, null, definition, cancellationToken) : null);
    }

    private static TriggerKind SourceTrigger(WorkItem item) =>
        item.Provenance.SourceKind == WorkSourceKind.ThoughtActivation ? TriggerKind.ThoughtActivation :
        item.Provenance.SourceKind == WorkSourceKind.ApplicationEvent
            ? TriggerKind.ApplicationEvent
            : TriggerKind.ScheduledOccurrence;
}
