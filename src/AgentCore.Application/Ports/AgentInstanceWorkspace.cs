using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Ports;

public sealed record AgentWorkspacePage(IReadOnlyList<AgentWorkspaceItem> Items, long? UsedBytes, long? TotalItems,
    string? NextPath, string UsageEnforcement = "sampled", bool Truncated = false);
public sealed record AgentWorkspaceContent(AgentWorkspaceItem Item, byte[] Bytes);

/// <summary>Path-addressed current filesystem observations. Ownership is checked by the use case and adapter.</summary>
public interface IAgentInstanceWorkspaceStore
{
    ValueTask<AgentWorkspaceContent> InspectAsync(Guid instanceId, string path, int maxContentBytes, CancellationToken ct = default);
    ValueTask RequireDirectoryAsync(Guid instanceId, string path, CancellationToken ct = default);
    ValueTask<AgentWorkspacePage> ListAsync(Guid instanceId, string prefix, string? afterPath, int limit, CancellationToken cancellationToken = default);
    ValueTask<AgentWorkspacePage> FindFilesAsync(Guid instanceId, string query, string? afterPath, int limit, CancellationToken ct = default);
    ValueTask<AgentWorkspaceContent> ReadAsync(Guid instanceId, string path, CancellationToken cancellationToken = default);
    ValueTask<AgentWorkspaceItem> WriteFileAsync(Guid instanceId, string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default);
    ValueTask DeleteAsync(Guid instanceId, string path, CancellationToken cancellationToken = default);
    ValueTask DeleteInstanceContentAsync(Guid instanceId, CancellationToken cancellationToken = default);
    ValueTask DeleteInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default);
    ValueTask<WorkspaceStructureResult> StructureAsync(Guid instanceId, IReadOnlyList<WorkspaceStructuralOperation> operations,
        CancellationToken cancellationToken = default);
}
