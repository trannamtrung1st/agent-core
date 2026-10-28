using System.Collections.Immutable;

namespace AgentCore.Domain.Workflows;

/// <summary>Immutable incumbent event lineage. It confers no claim or execution authority.</summary>
public sealed record WorkflowEventCausation(Guid? RootAgentRunId, int TriggerDepth, ImmutableArray<Guid> VisitedAutomationIds)
{
    public void Validate()
    {
        if (RootAgentRunId == Guid.Empty || TriggerDepth is < 0 or > 4 || VisitedAutomationIds.IsDefault || VisitedAutomationIds.Length > 4
            || VisitedAutomationIds.Any(id => id == Guid.Empty) || VisitedAutomationIds.Distinct().Count() != VisitedAutomationIds.Length)
            throw new ArgumentException("Workflow source causation must retain bounded actual event lineage.");
    }
    public bool Equals(WorkflowEventCausation? other) => other is not null && RootAgentRunId == other.RootAgentRunId
        && TriggerDepth == other.TriggerDepth && VisitedAutomationIds.SequenceEqual(other.VisitedAutomationIds);
    public override int GetHashCode()
    { var hash = new HashCode(); hash.Add(RootAgentRunId); hash.Add(TriggerDepth); foreach (var id in VisitedAutomationIds) hash.Add(id); return hash.ToHashCode(); }
}
