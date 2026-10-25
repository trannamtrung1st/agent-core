using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Ports;

public sealed record AgentWorkspacePage(IReadOnlyList<AgentWorkspaceItem> Items, long? UsedBytes, long? TotalItems,
    string? NextPath, string UsageEnforcement = "sampled", bool Truncated = false);
public sealed record AgentWorkspaceContent(AgentWorkspaceItem Item, byte[] Bytes);
