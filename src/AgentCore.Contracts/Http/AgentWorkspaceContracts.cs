namespace AgentCore.Contracts.Http;

public sealed record AgentWorkspaceItemResponse(Guid ItemId, Guid AgentInstanceId, string LogicalPath, string ContentType,
    long ByteSize, string Sha256Hex, long Revision, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, Guid? SourceSessionId, bool Directory = false);
public sealed record AgentWorkspacePageResponse(IReadOnlyList<AgentWorkspaceItemResponse> Items, long UsedBytes, int TotalItems,
    string? NextPath, long MaxFileBytes, long MaxInstanceBytes, string? TreeSha256 = null);
