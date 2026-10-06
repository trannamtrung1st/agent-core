namespace AgentCore.Contracts.Http;

public sealed record AgentWorkspaceItemResponse(Guid ItemId, Guid AgentInstanceId, string LogicalPath, string ContentType,
    long ByteSize, string Sha256Hex, long Revision, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, Guid? SourceSessionId, bool Directory = false);
public sealed record AgentWorkspacePageResponse(IReadOnlyList<AgentWorkspaceItemResponse> Items, long UsedBytes, int TotalItems,
    string? NextPath, long MaxFileBytes, long MaxInstanceBytes, string? TreeSha256 = null);
public sealed record WorkspaceRetainRequest(string Source, string Destination, long? ExpectedRevision = null, string? ExpectedSha256 = null);
public sealed record WorkspaceCheckoutRequest(string Source, string? Destination = null, long? ExpectedRevision = null, string? ExpectedSha256 = null);
public sealed record WorkspaceCheckoutResponse(AgentWorkspaceItemResponse Source, string Destination, long ByteSize, string Sha256Hex);
