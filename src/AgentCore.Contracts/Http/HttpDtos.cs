namespace AgentCore.Contracts.Http;

public sealed record HealthResponse(string Status, string Profile, int ProtocolVersion);

public sealed record CreateSessionRequest(string AgentId, int? AgentVersion, string? Mode);

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
    string? PauseReason = null);

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
    string? PauseReason = null);

public sealed record SessionCatalogPageResponse(
    IReadOnlyList<SessionCatalogItemResponse> Items,
    string? NextCursor,
    bool HasMore);

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
