namespace AgentCore.Domain.Triggers;

/// <summary>Every observed registration participates in CAS, including unchanged ones.</summary>
public sealed record InstanceAutomationPolicyChange(Guid AutomationId, long ExpectedRevision, AutomationStatus Status,
    DateTimeOffset? NextOccurrenceAtUtc, string? SuspensionReason);
