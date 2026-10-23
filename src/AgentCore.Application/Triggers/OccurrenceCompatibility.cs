using AgentCore.Domain.Definitions;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Triggers;

public static class OccurrenceCompatibility
{
    public const string Schedule = "schedule";
    public const string ApplicationEvent = "applicationEvent";

    public static string SourceName(TriggerSourceKind sourceKind) =>
        sourceKind switch { TriggerSourceKind.CoreEvent => "coreEvent", TriggerSourceKind.ApplicationEvent => ApplicationEvent, _ => Schedule };

    public static bool Allows(AgentDefinition definition, TriggerSourceKind sourceKind)
    {
        if (sourceKind == TriggerSourceKind.ManualInvocation) return true;
        var policy = definition.TriggerPolicy;
        return policy is { Enabled: true }
            && policy.AllowedSourceKinds.Contains(SourceName(sourceKind), StringComparer.Ordinal);
    }
}
