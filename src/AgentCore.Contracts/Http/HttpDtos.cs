namespace AgentCore.Contracts.Http;

public sealed record HealthResponse(string Status, string Profile, int ProtocolVersion);

public sealed record CreateSessionRequest(
    string AgentId,
    int? AgentVersion,
    string? Mode,
    string? SpeechLocale = null,
    SessionModelChoiceRequest? Model = null);

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

public sealed record HostCreateSessionRequest(
    string AgentId,
    int? AgentVersion,
    string? Mode,
    string? SpeechLocale = null,
    HostSessionPurposeRequest? Purpose = null,
    HostSessionCompletionPolicyRequest? CompletionPolicy = null,
    long? MaxDurationSeconds = null,
    SessionModelChoiceRequest? Model = null);

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

public sealed record SessionModelChoiceRequest(string? Key = null, string? ReasoningEffort = null);

public sealed record SetSessionModelRequest(string? Key = null, string? ReasoningEffort = null);

public sealed record SessionModelSelectionResponse(
    string CatalogKey,
    string DisplayName,
    string SelectionSource,
    string? ReasoningEffort,
    string? ModelId = null);

public sealed record ModelCapabilityResponse(bool Tools, bool Vision, bool StructuredOutput, bool Reasoning);

public sealed record ModelDescriptorResponse(
    string Key,
    string DisplayName,
    bool Tools,
    bool Vision,
    bool StructuredOutput,
    bool Reasoning,
    IReadOnlyList<string> SupportedReasoningEfforts,
    string? DefaultReasoningEffort,
    string? ContextCategory = null,
    string? CostCategory = null);

public sealed record ModelCatalogResponse(string DefaultKey, IReadOnlyList<ModelDescriptorResponse> Models);

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
    SessionModelSelectionResponse? Model = null);

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
    IReadOnlyList<HistoryAttachmentResponse>? Attachments = null);

public sealed record HistoryPageResponse(
    IReadOnlyList<HistoryItemResponse> Items,
    long NextAfter,
    bool HasMore,
    bool HasOlder = false,
    long? NextBefore = null);

public static class OwnerCapabilityHeaders
{
    public const string Name = "X-AgentCore-Owner-Capability";
}

public sealed record OwnerCapabilityResponse(string Token, string IssuedAt);

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
    string? LifecycleStatus = null);

public sealed record SessionCatalogPageResponse(
    IReadOnlyList<SessionCatalogItemResponse> Items,
    string? NextCursor,
    bool HasMore);

public sealed record BulkDeleteSessionsResponse(int DeletedCount);

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

public sealed record WorkspaceNodeResponse(string LogicalPath, bool Directory, long ByteSize, bool Writable);

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
