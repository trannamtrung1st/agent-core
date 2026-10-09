namespace AgentCore.Infrastructure.Persistence;

public sealed class WebhookEventRecord
{
    public string ResourceId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int Kind { get; set; }
    public string EventKey { get; set; } = "";
    public string? CredentialHash { get; set; }
    public int Status { get; set; }
    public long Revision { get; set; }
    public long CreatedAtUtc { get; set; }
    public long UpdatedAtUtc { get; set; }
}

public sealed class ExternalEventRecord
{
    public string EventId { get; set; } = "";
    public string ResourceId { get; set; } = "";
    public string SourceEventId { get; set; } = "";
    public long OccurredAtUtc { get; set; }
    public long AdmittedAtUtc { get; set; }
    public string EvidenceJson { get; set; } = "";
}

public sealed class ExternalEventDeliveryRecord
{
    public string? SnapshotJson { get; set; }
    public string? DecisionJson { get; set; }
    public string EventId { get; set; } = "";
    public string AutomationId { get; set; } = "";
    public string AgentInstanceId { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public int Status { get; set; }
}
