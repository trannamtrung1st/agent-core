namespace AgentCore.Contracts.Http;

public sealed record TenantApprovalPageDto(IReadOnlyList<TenantApprovalDto> Items, int Total, int Page, int PageSize);
public sealed record TenantApprovalDto(Guid RequestId, Guid TenantId, Guid RunId, Guid SessionId, Guid RootActivationId,
    Guid ExecutorPrincipalId, Guid? OriginalInitiatorPrincipalId, string Kind, string Status, long RequestRevision,
    long SecurityRevision, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string Summary, string Permission,
    TenantApprovalTargetDto TargetResource, TenantPreparedActionDto PreparedAction, TenantApprovalDecisionDto? Decision,
    TenantApprovalOperationResultDto? OperationResult, TenantApprovalReviewDto? Review);
public sealed record TenantApprovalTargetDto(string Type, string Id);
public sealed record TenantPreparedActionDto(Guid OperationId, string Operation, TenantPreparedPersonaDto? Persona,
    TenantPreparedPreferencesDto? Preferences, long ExpectedInstanceRevision, long ExpectedPersonaRevision, string ActionHash);
public sealed record TenantPreparedPersonaDto(string Name, string Role, string Description, string Tone);
public sealed record TenantPreparedPreferencesDto(string? ResponseLength, string? Language, bool? AskOneQuestionAtATime,
    bool ClearResponseLength, bool ClearLanguage, bool ClearAskOneQuestionAtATime);
public sealed record TenantApprovalDecisionDto(Guid PrincipalId, string Status, DateTimeOffset DecidedAt);
public sealed record TenantApprovalOperationResultDto(Guid OperationId, string Outcome, string ReasonCode,
    long InstanceRevision, long PersonaRevision, DateTimeOffset RecordedAt);
public sealed record TenantApprovalChangeDto(string Field, object? Before, object? After, bool BeforeAvailable, bool AfterAvailable, bool RestoresDefault);
public sealed record TenantApprovalReviewDto(string AgentName, string? RequesterName, string? ReviewerName,
    IReadOnlyList<TenantApprovalChangeDto> Changes, bool CanDecide, bool CanCancel, string? DecisionUnavailableReason,
    bool IndependentReview, DateTimeOffset ServerNow, string ExecutionState);
