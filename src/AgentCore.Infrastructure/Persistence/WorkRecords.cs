namespace AgentCore.Infrastructure.Persistence;

public sealed class WorkItemRecord
{
    public string WorkItemId { get; set; } = "";

    public string AgentInstanceId { get; set; } = "";

    public string ProfileId { get; set; } = "";

    public int Status { get; set; }

    public long Revision { get; set; }

    public int AttemptCount { get; set; }

    public int MaxAttempts { get; set; }

    public long? NextRetryAtUtc { get; set; }

    public string? ClaimGeneration { get; set; }

    public long? ClaimedAtUtc { get; set; }

    public long? ClaimLeaseExpiresAtUtc { get; set; }

    public bool CancellationRequested { get; set; }

    public long? CancellationRequestedAtUtc { get; set; }

    public string? KnownEffectSummary { get; set; }

    public string? ProgressSummary { get; set; }

    public long? ProgressUpdatedAtUtc { get; set; }

    public string? CheckpointJson { get; set; }

    public int? CheckpointStepCount { get; set; }

    public long? CheckpointOutputBytes { get; set; }

    public int? CheckpointRemainingOverallBudgetMs { get; set; }

    public string? ResultText { get; set; }

    public long? ResultCompletedAtUtc { get; set; }

    public string? FailureCode { get; set; }

    public string? FailureSummary { get; set; }

    public long? FailureAtUtc { get; set; }

    public int SideEffectDisposition { get; set; }

    public string? SideEffectActionHash { get; set; }

    public long? SideEffectUpdatedAtUtc { get; set; }

    public string? CurrentApprovalId { get; set; }

    public string SourceOccurrenceId { get; set; } = "";

    public int SourceKind { get; set; }

    public string? RegistrationId { get; set; }

    public string? SourceSessionId { get; set; }

    public string? SourceEventId { get; set; }

    public string DedupeKey { get; set; } = "";

    public long? ScheduledAtUtc { get; set; }

    public long ObservedAtUtc { get; set; }

    public string EvidenceJson { get; set; } = "";

    public string DefinitionId { get; set; } = "";

    public int DefinitionVersion { get; set; }

    public string PersonaName { get; set; } = "";

    public string ModelCatalogKey { get; set; } = "";

    public string ModelProviderAlias { get; set; } = "";

    public string ModelId { get; set; } = "";

    public string? ModelReasoningEffort { get; set; }

    public long CreatedAtUtc { get; set; }

    public long UpdatedAtUtc { get; set; }
}

public sealed class WorkApprovalRecord
{
    public string ApprovalId { get; set; } = "";

    public string WorkItemId { get; set; } = "";

    public string ExecutionGeneration { get; set; } = "";

    public long CheckpointRevision { get; set; }

    public string ToolName { get; set; } = "";

    public string PreparedActionJson { get; set; } = "";

    public string ActionHash { get; set; } = "";

    public string Preview { get; set; } = "";

    public long ExpiresAtUtc { get; set; }

    public int Decision { get; set; }

    public long? DecidedAtUtc { get; set; }

    public bool Consumed { get; set; }

    public long Revision { get; set; }

    public long CreatedAtUtc { get; set; }
}
