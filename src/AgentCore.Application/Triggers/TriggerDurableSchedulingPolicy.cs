using AgentCore.Domain.Definitions;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Triggers;

/// <summary>Definition-only scheduling rules; owning tenant services resolve current authority.</summary>
public static class TriggerDurableSchedulingPolicy
{
    public static bool AllowsScheduledOccurrence(AgentDefinition? definition) =>
        definition is not null && OccurrenceCompatibility.Allows(definition, TriggerSourceKind.Schedule);
}
