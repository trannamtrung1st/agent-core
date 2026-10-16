namespace AgentCore.Contracts.Http;

public sealed record AgentWorkspaceItemResponse(Guid AgentInstanceId, string LogicalPath, string ContentType,
    long ByteSize, DateTimeOffset UpdatedAt, bool Directory = false, string Kind = "file", string? LinkTarget = null, uint? Mode = null);
public sealed record AgentWorkspacePageResponse(IReadOnlyList<AgentWorkspaceItemResponse> Items, long? UsedBytes, long? TotalItems,
    string? NextPath, long MaxFileBytes, long MaxInstanceBytes, string UsageEnforcement, bool Truncated);
