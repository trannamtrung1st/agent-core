using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Workspaces;

// The retained Space tombstone is the cleanup mandate. Files stay outside the database.
public sealed class TenantSpaceWorkspaceCleanupService(ITenantSpaceStore spaces, ITenantSpaceWorkspaceCleanup files,
    ILogger<TenantSpaceWorkspaceCleanupService>? logger = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? cursor;
    public async ValueTask<int> RecoverAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var page = await spaces.ReadDeletedSpaceWorkspacePageAsync(cursor, 25, ct).ConfigureAwait(false); var completed = 0;
            foreach (var owner in page.Items)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (!await spaces.CanPurgeDeletedSpaceWorkspaceAsync(owner, ct).ConfigureAwait(false)) continue;
                    await files.PurgeDeletedSpaceAsync(owner, ct).ConfigureAwait(false); completed++;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception error) { HandledFailureLog.Warning(logger, error, "space.cleanup", "space_workspace_cleanup_pending"); }
            }
            cursor = page.NextCursor; return completed;
        }
        finally { gate.Release(); }
    }
}
