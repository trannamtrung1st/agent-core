namespace AgentCore.Contracts.Http;

public sealed record ArtifactPageResponse(IReadOnlyList<ArtifactResponse> Items, string? NextCursor, bool HasMore);

public sealed record AgentRunApprovalResponse(string ApprovalId, long Revision, string ActionHash,
    string ToolName, string Preview, string ExpiresAt);
public sealed record AgentRunOutcomeResponse(string Kind, string Summary, string? OutcomeEntryId, bool AttentionRequired);
public sealed record AgentRunWaitResponse(string Mode, string Until, IReadOnlyList<string> BackgroundSessionIds, string Deadline);
public sealed record AgentRunResponse(string AgentRunId, string SessionId, string ActivationId, string ActivationKind,
    string Status, long Revision, int AttemptCount, int MaxAttempts, bool CancellationRequested, bool CancellationAvailable,
    string? Progress, string? NextRetryAt, string CreatedAt, string UpdatedAt, AgentRunApprovalResponse? Approval,
    AgentRunOutcomeResponse? Outcome, string? FailureCode, string? FailureSummary, string? DiagnosticId,
    string? KnownEffectSummary, string ModelCatalogKey, string? ResponseId, string? AutomationId, string? ExperienceId, string? SourceOccurrenceId, string? SourceBackgroundSessionId = null, AgentRunWaitResponse? Wait = null, AgentRunBudgetResponse? Budget = null, AgentRunConfigurationResponse? Configuration = null, AutomationTriggerOriginResponse? TriggerOrigin = null);
public sealed record AgentRunConfigurationResponse(string DefinitionId, int DefinitionVersion, long InstanceRevision,
    long PersonaRevision, string ConfigurationHash, IReadOnlyList<AgentRunResourceResponse> Resources);
public sealed record AgentRunResourceResponse(string Key, string VirtualPath, string ContentSha256, long ByteLength);
public sealed record AgentRunPageResponse(IReadOnlyList<AgentRunResponse> Items, string? NextCursor, bool HasMore);
public sealed record BackgroundSessionOriginResponse(string Kind, string InitialAgentRunId, string? ParentSessionId,
    string? ParentAgentRunId, string? AutomationId, string? OccurrenceId, bool ReportCompletion);
public sealed record CompletionDeliveryResponse(string Status, string? TargetSessionId, string? ParentAgentRunId, string? Reason);
public sealed record BackgroundSessionResponse(SessionCatalogItemResponse Session, BackgroundSessionOriginResponse Origin,
    IReadOnlyList<string> Surfaces, AgentRunResponse? InitialRun, bool CanContinueInChat, int ArtifactCount, bool ArtifactCountHasMore, CompletionDeliveryResponse? CompletionDelivery = null, string? OriginalTitle = null);
public sealed record BackgroundSessionPageResponse(IReadOnlyList<BackgroundSessionResponse> Items, string? NextCursor, bool HasMore);
public sealed record ContinueInChatResponse(string SessionId);
public sealed record CancelAgentRunRequest(long ExpectedRevision);
public sealed record DecideAgentRunApprovalRequest(long ExpectedRevision, long ExpectedApprovalRevision, string ActionHash);

public sealed record InstanceActivitySessionResponse(SessionCatalogItemResponse Session, string Origin, IReadOnlyList<string> Surfaces);
public sealed record InstanceActivitySessionPageResponse(IReadOnlyList<InstanceActivitySessionResponse> Items, string? NextCursor, bool HasMore);

public sealed record AgentRunBudgetResponse(string Class, string Source, int MaxSteps, int DurationSeconds, int PerToolSeconds,
    int StepsConsumed, int ActiveExecutionMs, string Phase, string? TerminationReason, string CleanupStatus, bool ClosureConfirmed, bool? ClosureRequested = null, bool? LogoutRequested = null,
    bool LogoutVerified = false, bool CleanupBlocked = false);

public sealed record AutomationTriggerOriginResponse(string? TriggerId, string Kind, EventSourceReferenceDto? Source, string Summary, long? TriggerRevision = null);
