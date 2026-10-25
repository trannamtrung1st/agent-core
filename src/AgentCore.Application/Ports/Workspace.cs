using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Ports;

public sealed record WorkspaceNode(string LogicalPath, bool Directory, long ByteSize, bool Writable,
    string Kind = "file", DateTimeOffset? ModifiedAt = null, string? LinkTarget = null, uint? Mode = null);

public sealed record WorkspaceContent(string LogicalPath, string ContentType, byte[] Bytes);
public sealed record WorkspaceNodePage(IReadOnlyList<WorkspaceNode> Entries, string? NextPath, bool Truncated);

public sealed record WorkspaceTextEdit(string OldText, string NewText);

public sealed record WorkspacePatchResult(
    string LogicalPath,
    long ByteSize,
    int EditsApplied);

public interface ISessionWorkspace
{

    ValueTask EnsureAsync(Guid sessionId, AgentDefinition definition, CancellationToken cancellationToken = default);
    ValueTask RequireDirectoryAsync(Guid sessionId, AgentDefinition definition, string path, CancellationToken ct = default) =>
        throw new NotSupportedException("Workspace directory inspection is unavailable.");

    ValueTask<IReadOnlyList<WorkspaceNode>> ListAsync(
        Guid sessionId,
        AgentDefinition definition,
        string prefix,
        CancellationToken cancellationToken = default);
    async ValueTask<WorkspaceNodePage> ListPageAsync(Guid sessionId, AgentDefinition definition, string prefix,
        string? cursor, int limit, CancellationToken ct = default)
    {
        var entries = (await ListAsync(sessionId, definition, prefix, ct)).OrderBy(node => node.LogicalPath, StringComparer.Ordinal)
            .Where(node => cursor is null || string.CompareOrdinal(node.LogicalPath, cursor) > 0).Take(limit + 1).ToArray();
        return new(entries.Take(limit).ToArray(), entries.Length > limit ? entries[limit - 1].LogicalPath : null, entries.Length > limit);
    }

    ValueTask<WorkspaceContent> ReadAsync(
        Guid sessionId,
        AgentDefinition definition,
        string logicalPath,
        CancellationToken cancellationToken = default);

    /// <summary>Atomically publishes a new file; false means the name is already occupied (including links/directories).</summary>
    ValueTask<bool> TryWriteNewAsync(Guid sessionId, string logicalPath, ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default) => throw new NotSupportedException("Exclusive workspace creation is unavailable.");

    ValueTask WriteAsync(
        Guid sessionId,
        string logicalPath,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default);

    ValueTask<WorkspacePatchResult> PatchTextAsync(
        Guid sessionId,
        AgentDefinition definition,
        string logicalPath,
        IReadOnlyList<WorkspaceTextEdit> edits,
        CancellationToken cancellationToken = default);

    ValueTask<WorkspaceStructureResult> StructureAsync(Guid sessionId, IReadOnlyList<WorkspaceStructuralOperation> operations,
        CancellationToken cancellationToken = default) => throw new NotSupportedException("Structural filesystem operations are unavailable.");

    ValueTask DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);
}
