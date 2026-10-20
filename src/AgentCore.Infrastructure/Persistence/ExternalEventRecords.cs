namespace AgentCore.Infrastructure.Persistence;

public sealed class WebhookEventRecord
{
    public Guid? TenantId { get; set; }
    public Guid? CreatedByPrincipalId { get; set; }
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
    public string? SubscriptionsJson { get; set; }
    public Guid? TenantId { get; set; }
    public long? SourceRevision { get; set; }
    public string EventId { get; set; } = "";
    public string ResourceId { get; set; } = "";
    public string SourceEventId { get; set; } = "";
    public long OccurredAtUtc { get; set; }
    public long AdmittedAtUtc { get; set; }
    public string EvidenceJson { get; set; } = "";
}

public sealed class ExternalEventDeliveryRecord
{
    public string? Code { get; set; }
    public string? ReservedOccurrenceId { get; set; }
    public Guid? TenantId { get; set; }
    public Guid? ExecutorPrincipalId { get; set; }
    public string TriggerId { get; set; } = "";
    public string? SnapshotJson { get; set; }
    public string? DecisionJson { get; set; }
    public string EventId { get; set; } = "";
    public string AutomationId { get; set; } = "";
    public string AgentInstanceId { get; set; } = "";
    public string? ProfileId { get; set; } = "";
    public int Status { get; set; }
}
