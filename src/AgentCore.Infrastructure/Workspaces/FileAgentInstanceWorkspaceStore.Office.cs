using AgentCore.Application.Ports;
using AgentCore.Application.Security;

namespace AgentCore.Infrastructure.Workspaces;

public sealed partial class FileAgentInstanceWorkspaceStore
{
    public async ValueTask TransformOfficeTenantAsync(TenantWorkspaceReference owner, string path, bool create,
        Func<ReadOnlyMemory<byte>, CancellationToken, byte[]> process, Func<CancellationToken, ValueTask> authorizeCommit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(process); ArgumentNullException.ThrowIfNull(authorizeCommit);
        path = Concrete(owner, path);
        var files = await TenantFiles(owner, ct, write: true).ConfigureAwait(false);
        async ValueTask Gate(CancellationToken token)
        {
            await TenantFiles(owner, token, write: true).ConfigureAwait(false); await authorizeCommit(token).ConfigureAwait(false);
            await TenantFiles(owner, token, write: true).ConfigureAwait(false); token.ThrowIfCancellationRequested();
        }
        await Gate(ct).ConfigureAwait(false);
        var bound = Math.Min(maxFileBytes, OfficeLimits.MaxPackageBytes);
        var original = create ? [] : await files.ReadAsync(path, bound, ct).ConfigureAwait(false);
        await Gate(ct).ConfigureAwait(false);
        var bytes = process(original, ct);
        ct.ThrowIfCancellationRequested();
        if (create)
        {
            if (!await files.TryWriteNewAsync(path, bytes, bound, RootBudget(owner), ct, Gate).ConfigureAwait(false))
                throw new SecurityConflictException("workspace_source_changed");
        }
        else await files.WriteAsync(path, bytes, bound, RootBudget(owner), ct, async token => {
            await Gate(token).ConfigureAwait(false);
            var current = await files.ReadAsync(path, bound, token).ConfigureAwait(false);
            if (!current.AsSpan().SequenceEqual(original)) throw new SecurityConflictException("workspace_source_changed");
            await Gate(token).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }
}
