using AgentCore.Application.Ports;
using AgentCore.Application.Security;

namespace AgentCore.Infrastructure.Workspaces;

public sealed partial class FileAgentInstanceWorkspaceStore : ITenantWorkspaceMutationStore
{
    public async ValueTask<bool> TryWriteNewTenantAsync(TenantWorkspaceReference owner, string path, ReadOnlyMemory<byte> bytes,
        Func<CancellationToken, ValueTask> authorizeCommit, CancellationToken ct = default)
    {
        path = TenantWorkspaceRules.Path(owner, path, file: true);
        var files = await TenantFiles(owner, ct, write: true).ConfigureAwait(false);
        return await files.TryWriteNewAsync(path, bytes, maxFileBytes, RootBudget(owner), ct,
            async token => { await TenantFiles(owner, token, write: true).ConfigureAwait(false); await authorizeCommit(token).ConfigureAwait(false); }).ConfigureAwait(false);
    }
    public async ValueTask WriteTenantAsync(TenantWorkspaceReference owner, string path, ReadOnlyMemory<byte> bytes,
        Func<CancellationToken, ValueTask> authorizeCommit, CancellationToken ct = default)
    {
        path = TenantWorkspaceRules.Path(owner, path, file: true);
        var files = await TenantFiles(owner, ct, write: true).ConfigureAwait(false);
        await files.WriteAsync(path, bytes, maxFileBytes, owner.SessionId is null ? maxInstanceBytes : AgentCore.Domain.Conversation.WorkspaceLimits.MaxWritableBytes,
            ct, async token => { await TenantFiles(owner, token, write: true).ConfigureAwait(false); await authorizeCommit(token).ConfigureAwait(false); }).ConfigureAwait(false);
    }
    public async ValueTask DeleteTenantAsync(TenantWorkspaceReference owner, string path, Func<CancellationToken, ValueTask> authorizeCommit, CancellationToken ct = default)
    {
        path = TenantWorkspaceRules.Path(owner, path, file: true);
        var files = await TenantFiles(owner, ct, write: true).ConfigureAwait(false);
        await files.DeleteAuthorizedAsync(path, async token => { await TenantFiles(owner, token, write: true).ConfigureAwait(false); await authorizeCommit(token).ConfigureAwait(false); }, ct).ConfigureAwait(false);
    }
}
