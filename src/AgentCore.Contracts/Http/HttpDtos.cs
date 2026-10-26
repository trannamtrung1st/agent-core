using System.Text.Json.Serialization;

namespace AgentCore.Contracts.Http;

public sealed record HealthResponse(string Status, string Profile, int ProtocolVersion, string? BuildVersion = null, SchedulerReadinessResponse? Scheduler = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TenantSessionCreateRequest(Guid AgentInstanceId, long ExpectedRevision, long ExpectedInstanceRevision,
    string Mode = "text", ModelPreferenceDto? ModelPreference = null, string? RetentionKind = null);

/// <param name="Source">Ignored on all lifecycle routes; authority is fixed per route (user vs host).</param>
public sealed record TransitionLifecycleRequest(string Target, string? Source = null, string? Reason = null);

public sealed record HostSessionPurposeRequest(
    string? Kind = null,
    string? Description = null,
    string? DeadlineAt = null);

public sealed record HostSessionCompletionPolicyRequest(
    string? AgentCompletion = null,
    bool? UserCompletionAllowed = null,
    bool? UserCancellationAllowed = null);

public sealed record HostSessionPurposeResponse(string Kind, string? Description, string? DeadlineAt);

public sealed record HostSessionCompletionPolicyResponse(
    string AgentCompletion,
    bool UserCompletionAllowed,
    bool UserCancellationAllowed);

public sealed record HostSessionViewResponse(
    string SessionId,
    string AgentId,
    int AgentVersion,
    string Mode,
    string? PendingMode,
    string Status,
    string CreatedAt,
    string UpdatedAt,
    long LastEntrySequence,
    string? ActiveResponseId,
    int ProtocolVersion,
    string? PauseReason,
    string? LifecycleStatus,
    SpeechLocaleResponse? SpeechLocale,
    SessionModelSelectionResponse? Model,
    HostSessionPurposeResponse Purpose,
    HostSessionCompletionPolicyResponse CompletionPolicy,
    string? LifecycleSource = null,
    string? LifecycleReason = null,
    string? LifecycleChangedAt = null);

public sealed record SetSpeechLocaleRequest(string? Locale);

public sealed record SpeechLocaleResponse(string Effective, string Source, string? Override);

public sealed record SessionModelSelectionResponse(
    string OfferingId,
    string DisplayName,
    string SelectionSource,
    string? ReasoningEffort,
    string? ExternalModelId = null,
    string? ConnectionId = null, string? ConnectionLabel = null, bool ObservedFromRun = false);

public sealed record ModelCapabilityResponse(bool Tools, bool Vision, bool StructuredOutput, bool Reasoning);

public sealed record ModelDescriptorResponse(
    string OfferingId,
    string DisplayName,
    bool Tools,
    bool Vision,
    bool StructuredOutput,
    bool Reasoning,
    IReadOnlyList<string> SupportedReasoningEfforts,
    string? DefaultReasoningEffort,
    string? ContextCategory = null,
    string? CostCategory = null,
    string? ConnectionId = null, string? ConnectionLabel = null, string? ExternalModelId = null);

public sealed record ModelCatalogResponse(string? DefaultOfferingId, IReadOnlyList<ModelDescriptorResponse> Models, string? DefaultReasoningEffort = null, string DefaultSource = "tenantDefault");

public sealed record SessionViewResponse(
    string SessionId,
    string AgentId,
    int AgentVersion,
    string Mode,
    string? PendingMode,
    string Status,
    string CreatedAt,
    string UpdatedAt,
    long LastEntrySequence,
    string? ActiveResponseId,
    int ProtocolVersion,
    string? PauseReason = null,
    string? LifecycleStatus = null,
    SpeechLocaleResponse? SpeechLocale = null,
    SessionModelSelectionResponse? Model = null,
    string? AgentInstanceId = null,
    long? PinnedPersonaRevision = null,
    string? AgentName = null,
    string? AgentRole = null);

public sealed record AgentDescriptorResponse(
    string Id,
    int Version,
    string Name,
    string Role,
    string Description,
    bool VoiceAvailable,
    string Language = "en");

public sealed record AgentListResponse(IReadOnlyList<AgentDescriptorResponse> Agents);

public sealed record HistoryBlockResponse(
    string BlockId,
    string Kind,
    string Text,
    string FallbackText,
    string? AttachmentId,
    string? ArtifactId);

public sealed record HistoryAttachmentResponse(string AttachmentId, string DisplayName, string ContentType);

public sealed record HistoryMemoryReceiptResponse(
    string Outcome,
    string Operation,
    string Subject,
    IReadOnlyList<string>? Scopes,
    string Presentation,
    string Label);

public sealed record HistoryItemResponse(
    string EntryId,
    long Sequence,
    string? SourceEventId,
    string Role,
    string Text,
    string? ResponseId,
    string Status,
    string DeliveryMode,
    int HeardTextEndExclusive,
    int ReceivedTextEndExclusive,
    string CreatedAt,
    IReadOnlyList<HistoryBlockResponse>? Blocks = null,
    string? FinishReason = null,
    IReadOnlyList<HistoryAttachmentResponse>? Attachments = null,
    string? SpeechText = null,
    string? InterruptReason = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    HistoryFailureResponse? Failure = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<HistoryMemoryReceiptResponse>? MemoryReceipts = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? CompletedAt = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<AgentCore.Contracts.Realtime.UserMessagePartDto>? Parts = null);

public sealed record HistoryFailureResponse(
    string DiagnosticId,
    string? CorrelationId,
    string Category,
    string Code,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? FailureReason = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ProviderResponseChannel = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ProtocolRepair = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ProtocolRepairOutcome = null);

public sealed record HistoryPageResponse(
    IReadOnlyList<HistoryItemResponse> Items,
    long NextAfter,
    bool HasMore,
    bool HasOlder = false,
    long? NextBefore = null);

public sealed record RenameSessionRequest(string Title);

public sealed record DurableDeleteSessionRequest(long ExpectedRevision);

public sealed record SessionCatalogItemResponse(
    string SessionId,
    string Title,
    string AgentId,
    int AgentVersion,
    string Status,
    bool Archived,
    bool Ended,
    bool WorkspaceOwned,
    long RuntimeEpoch,
    long Revision,
    string CreatedAt,
    string UpdatedAt,
    string? PauseReason = null,
    string? LifecycleStatus = null,
    string? AgentName = null,
    string? AgentRole = null, string? AgentInstanceId = null);

public sealed record SessionCatalogPageResponse(
    IReadOnlyList<SessionCatalogItemResponse> Items,
    string? NextCursor,
    bool HasMore);

public sealed record BulkDeleteSessionsResponse(int DeletedCount);

public sealed record UserProfileValueResponse(string Value, string Source, string UpdatedAt);

public sealed record UserProfileResponse(
    long Revision,
    IReadOnlyDictionary<string, UserProfileValueResponse> Values);

public sealed record PatchUserProfileRequest(
    long ExpectedRevision,
    IReadOnlyDictionary<string, string?>? Values);

public sealed record AttachmentResponse(
    string AttachmentId,
    string SessionId,
    string DisplayName,
    string ContentType,
    long ByteSize,
    string Sha256,
    string State,
    bool Readable,
    string? EntryId,
    string CreatedAt,
    string? ExpiresAt);

public sealed record StageAttachmentsRequest(IReadOnlyList<string> AttachmentIds);

public sealed record KnowledgeDocumentResponse(
    string Identity,
    string Title,
    string Citation,
    string Content,
    string? SourceVersion,
    string RetrievedAt);

public sealed record WorkspaceNodeResponse(string LogicalPath, bool Directory, long ByteSize, bool Writable, string Kind, DateTimeOffset? ModifiedAt, string? LinkTarget, uint? Mode);
public sealed record WorkspaceNodePageResponse(IReadOnlyList<WorkspaceNodeResponse> Entries, string? NextPath, bool Truncated);

public sealed record SessionAutomationResponse(
    string AutomationId,
    string Instructions,
    string Status,
    string TriggerKind,
    string? TimeZone,
    string When,
    string? NextOccurrenceAt,
    long Revision,
    string? SuspensionReason = null,
    string Name = "", AutomationExecutionTargetDto? ExecutionTarget = null, AutomationCompletionDeliveryDto? CompletionDelivery = null, string? AgentInstanceId = null);

public sealed record SessionAutomationListResponse(IReadOnlyList<SessionAutomationResponse> Items);

public sealed record CancelTriggerRequest(long ExpectedRevision);

public sealed record ArtifactResponse(
    string ArtifactId,
    string SessionId,
    string DisplayName,
    string ContentType,
    long ByteSize,
    string Sha256,
    string? SourceAttachmentId,
    string? WorkspaceLogicalPath,
    string CreatedAt);

public sealed record ChatAgentInstanceResponse(
    string InstanceId,
    string DefinitionId,
    int ActiveVersion,
    string Name,
    string Role,
    bool VoiceAvailable,
    string Language);

public sealed record ChatAgentInstanceListResponse(IReadOnlyList<ChatAgentInstanceResponse> Items);

public sealed record SchedulerReadinessResponse(string Status, string? LastPassAt, string? LastSuccessAt);

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record TenantSessionModelRequest(long ExpectedRevision, long ExpectedSessionRevision, ModelPreferenceDto? ModelPreference = null);
