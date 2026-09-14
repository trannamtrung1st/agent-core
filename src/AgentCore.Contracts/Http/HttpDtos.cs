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
    int ProtocolVersion);

public sealed record AgentDescriptorResponse(
    string Id,
    int Version,
    string Name,
    string Role,
    string Description,
    bool VoiceAvailable);

public sealed record AgentListResponse(IReadOnlyList<AgentDescriptorResponse> Agents);

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
    string CreatedAt);

public sealed record HistoryPageResponse(
    IReadOnlyList<HistoryItemResponse> Items,
    long NextAfter,
    bool HasMore);
