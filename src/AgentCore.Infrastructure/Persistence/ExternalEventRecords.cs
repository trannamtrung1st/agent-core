namespace AgentCore.Infrastructure.Persistence;

public sealed class ExternalEventSourceRecord
{
    public string SourceId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int Kind { get; set; }
    public string SourceKey { get; set; } = "";
    public string? CredentialHash { get; set; }
    public int Status { get; set; }
    public long Revision { get; set; }
    public long CreatedAtUtc { get; set; }
    public long UpdatedAtUtc { get; set; }
}

public sealed class ExternalEventRecord
{
    public string EventId { get; set; } = "";
    public string SourceId { get; set; } = "";
    public string SourceEventId { get; set; } = "";
    public string EventType { get; set; } = "";
    public long OccurredAtUtc { get; set; }
    public long AdmittedAtUtc { get; set; }
    public string EvidenceJson { get; set; } = "";
}

public sealed class ExternalEventDeliveryRecord
{
    public string EventId { get; set; } = "";
    public string AutomationId { get; set; } = "";
    public string AgentInstanceId { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public int Status { get; set; }
}
