namespace AgentCore.Infrastructure.Persistence;

public sealed class ActivationRecord
{
    public string ActivationId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string DedupeKey { get; set; } = "";
    public string? BackgroundSourceKey { get; set; }
    public string AdmissionHash { get; set; } = "";
    public string AgentInstanceId { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public string PayloadJson { get; set; } = "";
    public long AdmittedAtUtc { get; set; }
}

public sealed class AgentRunRecord
{
    public string AgentRunId { get; set; } = "";
    public string ActivationId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string AgentInstanceId { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public int Status { get; set; }
    public long Revision { get; set; }
    public long? NextRetryAtUtc { get; set; }
    public long? LeaseExpiresAtUtc { get; set; }
    public long? ApprovalExpiresAtUtc { get; set; }
    public long CreatedAtUtc { get; set; }
    public long UpdatedAtUtc { get; set; }
    public string PayloadJson { get; set; } = "";
}

public sealed class ActivationSourceEntryRecord
{
    public string SessionId { get; set; } = "";
    public string EntryId { get; set; } = "";
    public string ActivationId { get; set; } = "";
    public int Ordinal { get; set; }
}

public sealed class BackgroundCompletionReceiptRecord
{
    public string ParentSessionId { get; set; } = "";
    public int Status { get; set; }
    public long? ClaimExpiresAtUtc { get; set; }
    public string? ClaimRunId { get; set; }
    public string? InboxJson { get; set; }
    public long Revision { get; set; } = 1;
    public string ChildAgentRunId { get; set; } = "";
    public string AgentInstanceId { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public string? ParentActivationId { get; set; }
    public string? SkipReason { get; set; }
    public long CreatedAtUtc { get; set; }
}
