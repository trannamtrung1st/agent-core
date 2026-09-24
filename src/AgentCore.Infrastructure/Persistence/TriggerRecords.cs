namespace AgentCore.Infrastructure.Persistence;

public sealed class TriggerRegistrationRecord
{
    public string RegistrationId { get; set; } = "";
    public string AgentInstanceId { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public int Status { get; set; }
    public string Intent { get; set; } = "";
    public int ScheduleKind { get; set; }
    public string ScheduleJson { get; set; } = "";
    public long? NextOccurrenceAtUtc { get; set; }
    public long? ExpiresAtUtc { get; set; }
    public int OccurrenceCount { get; set; }
    public long Revision { get; set; }
    public long ScheduleRevision { get; set; }
    public int AuthorizationOrigin { get; set; }
    public string? SourceSessionId { get; set; }
    public string? SourceEventId { get; set; }
    public long CreatedAtUtc { get; set; }
    public long UpdatedAtUtc { get; set; }
    public string? SuspensionReason { get; set; }
}

public sealed class TriggerOccurrenceRecord
{
    public string OccurrenceId { get; set; } = "";
    public string DedupeKey { get; set; } = "";
    public string? RegistrationId { get; set; }
    public string AgentInstanceId { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public int SourceKind { get; set; }
    public long? ScheduledAtUtc { get; set; }
    public long ObservedAtUtc { get; set; }
    public long AdmittedAtUtc { get; set; }
    public string EvidenceJson { get; set; } = "";
    public string? SourceEventId { get; set; }
    public long? ScheduleRevision { get; set; }
    public int Disposition { get; set; }
    public string? DispositionReason { get; set; }
    public long RoutingRevision { get; set; }
    public long? RoutingUpdatedAtUtc { get; set; }
    public string? ClaimId { get; set; }
    public long? ClaimLeaseExpiresAtUtc { get; set; }
    public string? DurableWorkItemId { get; set; }
}
