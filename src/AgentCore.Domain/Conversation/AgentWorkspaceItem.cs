namespace AgentCore.Domain.Conversation;

public sealed record AgentWorkspaceItem(
    Guid ItemId,
    Guid AgentInstanceId,
    string LogicalPath,
    string ContentType,
    long ByteSize,
    string Sha256Hex,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    Guid? SourceSessionId);

public static class AgentWorkspaceLimits
{
    public const long MaxFileBytes = 50L * 1024 * 1024;
    public const long MaxInstanceBytes = 250L * 1024 * 1024;
    public const int MaxItems = 4096;
    public const int MaxPathChars = 512;
    public const int MaxDepth = 8;
    public const int MaxPageItems = 256;
}
