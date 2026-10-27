namespace AgentCore.Contracts.Http;

public sealed record TenantApprovalPageDto(IReadOnlyList<TenantApprovalDto> Items, int Total, int Page, int PageSize);
public sealed record TenantApprovalDto(Guid RequestId, Guid TenantId, Guid? RunId, Guid? SessionId, Guid RootActivationId,
    Guid ExecutorPrincipalId, Guid? OriginalInitiatorPrincipalId, string Kind, string Status, long RequestRevision,
    long SecurityRevision, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string Summary, string Permission,
    TenantApprovalTargetDto TargetResource, TenantPreparedActionDto PreparedAction, TenantApprovalDecisionDto? Decision,
    TenantApprovalOperationResultDto? OperationResult, TenantApprovalReviewDto? Review,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] Guid? DestinationSessionId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] TenantHumanTaskApprovalOutcomeDto? TaskAdmissionOutcome = null);
public sealed record TenantHumanTaskApprovalOutcomeDto(bool Admitted, string ReasonCode, DateTimeOffset RecordedAt, TenantTaskAdmissionReceiptDto? TaskAdmission);
public sealed record TenantApprovalTargetDto(string Type, string Id);
public sealed record TenantPreparedActionDto(Guid OperationId, string Operation, TenantPreparedPersonaDto? Persona,
    TenantPreparedPreferencesDto? Preferences, long ExpectedInstanceRevision, long ExpectedPersonaRevision, string ActionHash,
    TenantPreparedCredentialCheckDto? CredentialCheck = null, TenantPreparedSandboxCommandDto? SandboxCommand = null);
public sealed record TenantPreparedSandboxCommandDto(Guid SessionId, string ProfileId, long ProfileRevision, string CommandJson,
    long ExpectedLeaseRevision, Guid? ExpectedGeneration, string HomeAccess);
public sealed record TenantPreparedCredentialCheckDto(Guid CredentialId, Guid BindingId, string Reference,
    long BindingRevision, long CredentialRevision);
public sealed record TenantPreparedPersonaDto(string Name, string Role, string Description, string Tone);
public sealed record TenantPreparedPreferencesDto(string? ResponseLength, string? Language, bool? AskOneQuestionAtATime,
    bool ClearResponseLength, bool ClearLanguage, bool ClearAskOneQuestionAtATime);
public sealed record TenantApprovalDecisionDto(Guid PrincipalId, string Status, DateTimeOffset DecidedAt);
public sealed record TenantApprovalOperationResultDto(Guid OperationId, string Outcome, string ReasonCode,
    long InstanceRevision, long PersonaRevision, DateTimeOffset RecordedAt,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] TenantBrowserArtifactPublicationDto? BrowserArtifacts = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] TenantAccountActionResultDto? AccountAction = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] TenantHttpReceiptDto? Http = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] TenantTaskAdmissionReceiptDto? TaskAdmission = null);
public sealed record TenantTaskAdmissionReceiptDto(Guid TaskId, long Revision);
public sealed record TenantHttpReceiptDto(int StatusCode,string FinalUrl,string? ContentType,long ProjectedBodyBytes,string BodySha256,bool Truncated);
public sealed record TenantAccountActionResultDto(string Status, string? DraftId, string? FileId, string? Path, long? ByteSize, string? Sha256, IReadOnlyList<TenantAccountActionItemResultDto>? Items);
public sealed record TenantAccountActionItemResultDto(string FileId, string Status);
public sealed record TenantBrowserArtifactPublicationDto(string Status, string? ReasonCode, IReadOnlyList<TenantBrowserArtifactReferenceDto> Files);
public sealed record TenantBrowserArtifactReferenceDto(string Kind, Guid ArtifactId);
public sealed record TenantApprovalChangeDto(string Field, object? Before, object? After, bool BeforeAvailable, bool AfterAvailable, bool RestoresDefault);
public sealed record TenantApprovalReviewDto(string AgentName, string? RequesterName, string? ReviewerName,
    IReadOnlyList<TenantApprovalChangeDto> Changes, bool CanDecide, bool CanCancel, string? DecisionUnavailableReason,
    bool IndependentReview, DateTimeOffset ServerNow, string ExecutionState);
