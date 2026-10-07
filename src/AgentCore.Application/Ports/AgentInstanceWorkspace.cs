using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Ports;

public sealed record AgentWorkspacePage(IReadOnlyList<AgentWorkspaceItem> Items, long UsedBytes, int TotalItems, string? NextPath, string? TreeSha256 = null);
public sealed record AgentWorkspaceContent(AgentWorkspaceItem Item, byte[] Bytes);
public sealed record AgentWorkspaceCheckout(AgentWorkspaceItem Source, string Destination, long ByteSize, string Sha256Hex);

/// <summary>Dedicated managed-instance storage. Provenance never owns or cascades into these bytes.</summary>
public interface IAgentInstanceWorkspaceStore
{
    ValueTask<WorkspaceTransfer> ExportAsync(Guid instanceId, string path, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Workspace tree export is unavailable.");
    ValueTask ImportAsync(Guid instanceId, string destination, WorkspaceTransfer transfer, Guid? sourceSessionId = null,
        long? expectedRevision = null, string? expectedSha256 = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Workspace tree import is unavailable.");

    ValueTask<AgentWorkspacePage> ListAsync(Guid instanceId, string prefix, string? afterPath, int limit, CancellationToken cancellationToken = default);
    ValueTask<AgentWorkspaceContent> ReadAsync(Guid instanceId, Guid? itemId, string? path, CancellationToken cancellationToken = default);
    ValueTask<AgentWorkspaceItem> RetainAsync(Guid instanceId, string path, string contentType, ReadOnlyMemory<byte> bytes,
        Guid? sourceSessionId, long? expectedRevision, string? expectedSha256, CancellationToken cancellationToken = default);
    ValueTask DeleteAsync(Guid instanceId, Guid itemId, long expectedRevision, CancellationToken cancellationToken = default);
    ValueTask DeleteInstanceContentAsync(Guid instanceId, CancellationToken cancellationToken = default);
    ValueTask DeleteInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default);
    ValueTask<WorkspaceStructureResult> StructureAsync(Guid instanceId, IReadOnlyList<WorkspaceStructuralOperation> operations,
        string expectedTreeSha256, CancellationToken cancellationToken = default) => throw new NotSupportedException("Home filesystem operations are unavailable.");
}
