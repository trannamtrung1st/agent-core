using AgentCore.Domain.Security;

namespace AgentCore.Application.Ports;

public sealed record TenantWorkspaceHumanAuthorization(TenantWorkspaceReference Workspace, HumanSessionReference Human,
    long SecurityRevision, long InstanceRevision, string Permission, AuthorizationAudit Audit);

public interface ITenantWorkspaceMutationStore
{
    ValueTask WriteTenantAsync(TenantWorkspaceReference owner, string path, ReadOnlyMemory<byte> bytes,
        Func<CancellationToken, ValueTask> authorizeCommit, CancellationToken ct = default);
    ValueTask DeleteTenantAsync(TenantWorkspaceReference owner, string path,
        Func<CancellationToken, ValueTask> authorizeCommit, CancellationToken ct = default);
}
