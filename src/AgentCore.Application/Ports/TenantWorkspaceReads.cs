using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Ports;

public sealed record TenantWorkspaceReference(Guid TenantId, Guid InstanceId, Guid? SessionId = null)
{
    public string LogicalRoot => SessionId is null ? "/home" : "/working";
}

// Current filesystem observations, not persisted file identities or mutation/version tokens.
// The adapter independently resolves the actual owner; no root is created on a read.
public interface ITenantWorkspaceReadStore
{
    ValueTask<AgentWorkspacePage> ListTenantAsync(TenantWorkspaceReference owner, string path, string? cursor, int limit, CancellationToken ct = default);
    ValueTask<AgentWorkspacePage> SearchTenantAsync(TenantWorkspaceReference owner, string query, string? cursor, int limit, CancellationToken ct = default);
    ValueTask<AgentWorkspaceContent> InspectTenantAsync(TenantWorkspaceReference owner, string path, int maxContentBytes, CancellationToken ct = default);
}
