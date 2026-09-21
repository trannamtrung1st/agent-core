using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Ports;

public sealed record WorkspaceNode(string LogicalPath, bool Directory, long ByteSize, bool Writable);

public sealed record WorkspaceContent(string LogicalPath, string ContentType, byte[] Bytes);

public sealed record WorkspaceTextEdit(string OldText, string NewText);

public sealed record WorkspacePatchResult(
    string LogicalPath,
    string PreviousSha256Hex,
    string NewSha256Hex,
    long ByteSize,
    int EditsApplied);

public interface ISessionWorkspace
{
    ValueTask EnsureAsync(Guid sessionId, AgentDefinition definition, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<WorkspaceNode>> ListAsync(
        Guid sessionId,
        AgentDefinition definition,
        string prefix,
        CancellationToken cancellationToken = default);

    ValueTask<WorkspaceContent> ReadAsync(
        Guid sessionId,
        AgentDefinition definition,
        string logicalPath,
        CancellationToken cancellationToken = default);

    ValueTask WriteAsync(
        Guid sessionId,
        string logicalPath,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default);

    ValueTask<WorkspacePatchResult> PatchTextAsync(
        Guid sessionId,
        AgentDefinition definition,
        string logicalPath,
        string expectedSha256Hex,
        IReadOnlyList<WorkspaceTextEdit> edits,
        CancellationToken cancellationToken = default);

    ValueTask MoveAsync(
        Guid sessionId,
        string sourceLogicalPath,
        string destinationLogicalPath,
        CancellationToken cancellationToken = default);

    ValueTask DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);
}
