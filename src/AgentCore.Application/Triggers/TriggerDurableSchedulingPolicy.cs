using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Triggers;

public static class TriggerDurableSchedulingPolicy
{
    public static async ValueTask<AgentDefinition?> ResolveEffectiveDefinitionAsync(
        TriggerOwner owner,
        IAgentInstanceStore instances,
        IAgentDefinitionStore definitions,
        CancellationToken cancellationToken = default)
    {
        var instance = await instances.FindAsync(owner.AgentInstanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return null;
        }

        return await definitions.GetAsync(instance.DefinitionId, instance.ActiveVersion, cancellationToken).ConfigureAwait(false);
    }

    public static bool AllowsUserScheduling(AgentDefinition? definition) =>
        definition is not null
        && OccurrenceCompatibility.Allows(definition, TriggerSourceKind.Schedule)
        && definition.TriggerPolicy is { Enabled: true, AllowUserScheduling: true };

    public static bool AllowsScheduledOccurrence(AgentDefinition? definition) =>
        definition is not null && OccurrenceCompatibility.Allows(definition, TriggerSourceKind.Schedule);
}
