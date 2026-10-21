using AgentCore.Application.Ports;

namespace AgentCore.Infrastructure.Workspaces;

public sealed partial class FileSessionWorkspace : ITenantWorkingCleanupBytes
{
    private readonly SemaphoreSlim _workingCleanup = new(1, 1);

    public async ValueTask<bool> PurgeTenantWorkingAsync(TenantWorkingCleanupTarget target,
        Func<CancellationToken, ValueTask<bool>> stillAllowed, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!await stillAllowed(ct).ConfigureAwait(false)) return false;
        await _workingCleanup.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var gate = _provisioning.GetOrAdd(target.Owner.SessionId, _ => new(1, 1));
            var acquired = false;
            try
            {
                await gate.WaitAsync(ct).ConfigureAwait(false); acquired = true;
                // Repeat exact retained owner/policy/resource proof under the same native provisioning gate.
                if (!await stillAllowed(ct).ConfigureAwait(false)) return false;
                CanonicalWorkspaceFilesystem.PurgeOwner(AgentWorkspacePhysicalPaths.SessionRoot(_root,
                    target.Owner.AgentInstanceId, target.Owner.SessionId), ct);
                return true;
            }
            finally
            {
                if (acquired) gate.Release();
                // Terminal/deleted authority cannot provision again. Native purges also share one bounded gate.
                _provisioning.TryRemove(new KeyValuePair<Guid, SemaphoreSlim>(target.Owner.SessionId, gate));
            }
        }
        finally { _workingCleanup.Release(); }
    }
}
