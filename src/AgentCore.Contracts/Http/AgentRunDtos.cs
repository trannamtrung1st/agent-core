namespace AgentCore.Contracts.Http;

public sealed record ArtifactPageResponse(IReadOnlyList<ArtifactResponse> Items, string? NextCursor, bool HasMore);

public sealed record AgentRunApprovalResponse(string ApprovalId, long Revision, string ActionHash,
    string ToolName, string Preview, string ExpiresAt);
public sealed record AgentRunOutcomeResponse(string Kind, string Summary, string? OutcomeEntryId, bool AttentionRequired);
public sealed record AgentRunResponse(string AgentRunId, string SessionId, string ActivationId, string ActivationKind,
    string Status, long Revision, int AttemptCount, int MaxAttempts, bool CancellationRequested, bool CancellationAvailable,
    string? Progress, string? NextRetryAt, string CreatedAt, string UpdatedAt, AgentRunApprovalResponse? Approval,
    AgentRunOutcomeResponse? Outcome, string? FailureCode, string? FailureSummary, string? DiagnosticId,
    string? KnownEffectSummary, string ModelCatalogKey, string? ResponseId, string? AutomationId, string? ExperienceId, string? SourceOccurrenceId, string? SourceBackgroundSessionId = null);
public sealed record AgentRunPageResponse(IReadOnlyList<AgentRunResponse> Items, string? NextCursor, bool HasMore);
public sealed record BackgroundSessionOriginResponse(string Kind, string InitialAgentRunId, string? ParentSessionId,
    string? ParentAgentRunId, string? AutomationId, string? OccurrenceId, bool ReportCompletion);
public sealed record CompletionDeliveryResponse(string Status, string? TargetSessionId, string? ParentAgentRunId, string? Reason);
public sealed record BackgroundSessionResponse(SessionCatalogItemResponse Session, BackgroundSessionOriginResponse Origin,
    IReadOnlyList<string> Surfaces, AgentRunResponse? LatestRun, bool CanContinueInChat, int ArtifactCount, bool ArtifactCountHasMore, CompletionDeliveryResponse? CompletionDelivery = null);
public sealed record BackgroundSessionPageResponse(IReadOnlyList<BackgroundSessionResponse> Items, string? NextCursor, bool HasMore);
public sealed record ContinueInChatResponse(string SessionId);
public sealed record CancelAgentRunRequest(long ExpectedRevision);
public sealed record DecideAgentRunApprovalRequest(long ExpectedRevision, long ExpectedApprovalRevision, string ActionHash);
