namespace AgentCore.Application.Ports;

public sealed record WorkspaceStructuralOperation(string Op, string? Path = null, string? Source = null,
    string? Destination = null, bool Recursive = false);

public sealed record WorkspaceOperationOutcome(int Index, string Operation, string Kind, string Status,
    int FilesAffected, int DirectoriesAffected, long BytesAffected);

public sealed record WorkspaceStructureResult(bool Completed, int OperationCount, int CompletedCount,
    int? FailedIndex, IReadOnlyList<WorkspaceOperationOutcome> Results, string? ErrorCode = null,
    string? Message = null, bool MutationsMayHaveOccurred = false);

public static class WorkspaceStructureLimits
{
    // One small inspectable approval, bounded traversal and bounded per-operation outcomes.
    public const int MaxOperations = 16;
    public const int MaxPathChars = 4095;
    public const int MaxRequestBytes = 16 * 1024;
}
