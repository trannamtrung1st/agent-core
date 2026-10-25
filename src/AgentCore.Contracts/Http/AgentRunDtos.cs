using System.Text.Json.Serialization;

namespace AgentCore.Contracts.Http;

public sealed record ArtifactPageResponse(IReadOnlyList<ArtifactResponse> Items, string? NextCursor, bool HasMore);

public sealed record AgentRunOutcomeResponse(string Kind, string Summary, string? OutcomeEntryId, bool AttentionRequired);
public sealed record AgentRunWaitResponse(string Mode, string Until, IReadOnlyList<string> BackgroundSessionIds, string Deadline);
public sealed record AgentRunResponse(string AgentRunId, string SessionId, string ActivationId, string ActivationKind,
    string Status, long Revision, int AttemptCount, int MaxAttempts, bool CancellationRequested, bool CancellationAvailable,
    string? Progress, string? NextRetryAt, string CreatedAt, string UpdatedAt,
    AgentRunOutcomeResponse? Outcome, string? FailureCode, string? FailureSummary, string? DiagnosticId,
    string? KnownEffectSummary, string ModelOfferingId, string? ResponseId, string? AutomationId, string? ExperienceId, string? SourceOccurrenceId, string? SourceBackgroundSessionId = null, AgentRunWaitResponse? Wait = null, AgentRunBudgetResponse? Budget = null, AgentRunConfigurationResponse? Configuration = null, AutomationTriggerOriginResponse? TriggerOrigin = null, ComposerRunInputResponse? ComposerInput = null, RunExecutionDiagnosticResponse? ExecutionDiagnostic = null, ContextManifestResponse? ContextManifest = null);
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

public sealed record InstanceActivitySessionResponse(SessionCatalogItemResponse Session, string Origin, IReadOnlyList<string> Surfaces);
public sealed record InstanceActivitySessionPageResponse(IReadOnlyList<InstanceActivitySessionResponse> Items, string? NextCursor, bool HasMore);

public sealed record AgentRunBudgetResponse(string Class, string Source, int MaxSteps, int DurationSeconds, int PerToolSeconds,
    int StepsConsumed, int ActiveExecutionMs, string Phase, string? TerminationReason, string CleanupStatus, bool ClosureConfirmed, bool? ClosureRequested = null, bool? LogoutRequested = null,
    bool LogoutVerified = false, bool CleanupBlocked = false, int SandboxCommandSeconds = 120);

public sealed record AutomationTriggerOriginResponse(string? TriggerId, string Kind, EventSourceReferenceDto? Source, string Summary, long? TriggerRevision = null);

public sealed record ComposerRunInputResponse(IReadOnlyList<string> ExplicitSkillKeys, string Provenance, string AdmittedAt, string? Error, IReadOnlyList<ComposerReferenceResponse> References);
public sealed record ComposerReferenceResponse(string Kind, string Locator, string Label, string Status, long? Revision, string? Sha256, bool Truncated);

public sealed record RunAttemptDiagnosticResponse(int Attempt, string Code, string? Reason, string? Channel,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MaxOutputTokens = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Transport = null);
public sealed record RunProtocolRecoveryResponse(string Reason, string? Channel, string Eligibility, string? Strategy, string? Outcome);
public sealed record RunExecutionDiagnosticResponse(int RemainingBudgetMs, string? LastTool, int RecordedToolResults,
    IReadOnlyList<RunAttemptDiagnosticResponse> FailedAttempts, RunProtocolRecoveryResponse? ProtocolRecovery = null);

public sealed record ContextManifestResponse(int ContextFormatVersion, int CoreContractVersion,
    IReadOnlyList<ContextManifestSectionResponse> Sections, string Fingerprint, IReadOnlyDictionary<string, ContextFieldSourceResponse>? FieldSources = null);
public sealed record ContextManifestSectionResponse(string Id, string Layer, string Source, string Semantics,
    string? SourceRevision, int Characters, int Utf8Bytes, int EstimatedTokens, bool Omitted = false, int? OriginalCharacters = null);

public sealed record ContextFieldSourceResponse(string Kind, string Revision);
