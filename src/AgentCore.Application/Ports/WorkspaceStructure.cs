namespace AgentCore.Application.Ports;

public sealed record WorkspaceStructuralOperation(string Op, string? Path = null, string? Source = null,
    string? Destination = null, bool Recursive = false);

public sealed record WorkspaceOperationOutcome(int Index, string Operation, string Kind, string Status,
    int FilesAffected, int DirectoriesAffected, long BytesAffected);

public sealed record WorkspaceStructureResult(bool Completed, int OperationCount, int CompletedCount,
    int? FailedIndex, IReadOnlyList<WorkspaceOperationOutcome> Results, string? ErrorCode = null,
    string? Message = null, bool MutationsMayHaveOccurred = false, string? TreeSha256 = null);

public sealed record WorkspaceTreeEntry(string Path, bool Directory, long ByteSize);

public static class WorkspaceStructureLimits
{
    // One small inspectable approval, bounded traversal and bounded per-operation outcomes.
    public const int MaxOperations = 16;
    public const int MaxEntries = 8192;
    public const int MaxDepth = 8;
    public const int MaxPathChars = 512;
    public const int MaxRequestBytes = 16 * 1024;
}
