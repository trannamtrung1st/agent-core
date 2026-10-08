using AgentCore.Application.Models;
using AgentCore.Application.Agents;
using AgentCore.Application.Tools;
using AgentCore.Application.Ports;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Execution;

/// <summary>Pins preserve execution identity; current owner and source policy still grant authority.</summary>
public sealed class AgentRunAuthority(IAgentInstanceStore instances, IAgentDefinitionStore definitions,
    IMemoryStore memory, ITriggerStore triggers, IModelCatalog models) : IAgentRunAuthority
{
    public async ValueTask<AgentDefinition?> CurrentDefinitionAsync(AgentRun run, CancellationToken ct = default)
    {
        var instance = await instances.FindAsync(run.AgentInstanceId, ct).ConfigureAwait(false);
        if (instance is null || instance.Lifecycle != AgentInstanceLifecycle.Active
            || instance.DefinitionId != run.DefinitionId || await memory.LoadProfileAsync(run.ProfileId, ct).ConfigureAwait(false) is null) return null;
        var definition = await definitions.GetAsync(instance.DefinitionId, instance.ActiveVersion, ct).ConfigureAwait(false);
        if (definition is null) return null;
        var model = new ExecutionModelPin(run.PinnedModel.CatalogKey, run.PinnedModel.ProviderAlias,
            run.PinnedModel.ModelId, run.PinnedModel.ReasoningEffort, ExecutionModelSource.ConversationDefault);
        if (!ExecutionModelPolicy.Matches(models, model, out var descriptor)) return null;
        var activation = run.Admission.Activation;
        if (activation.Kind == ActivationKind.ImmediateBackground && (descriptor?.Tools != true || !RolePermissions.AllowsTool(definition, ToolCatalog.BackgroundStart))) return null;
        if (activation.TriggerOccurrenceId is { } occurrenceId)
        {
            var owner = new TriggerOwner(run.AgentInstanceId, run.ProfileId);
            var occurrence = await triggers.GetOccurrenceAsync(owner, occurrenceId, ct).ConfigureAwait(false);
            if (occurrence is null || !OccurrenceCompatibility.Allows(definition, AutomationRules.AdmissionSource(occurrence))) return null;
            Automation? automation = null;
            if (occurrence.AutomationId is { } id)
            {
                automation = await triggers.GetAsync(owner, id, ct).ConfigureAwait(false);
                if (automation is null || automation.Status is AutomationStatus.Disabled or AutomationStatus.Cancelled or AutomationStatus.SuspendedPolicy) return null;
            }
            if (run.Admission.OutputContract == AgentRunOutputContract.BackgroundOutcome
                && !ExecutionModelPolicy.Validate(models, model, definition, automation).Accepted) return null;
            if (automation?.RequiresTools == true && descriptor?.Tools != true) return null;
            if (automation?.RequiresVision == true && descriptor?.Vision != true) return null;
        }
        return definition;
    }
}
