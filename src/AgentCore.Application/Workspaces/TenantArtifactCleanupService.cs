using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Workspaces;

public sealed class TenantArtifactCleanupService(ITenantArtifactCleanupStore store, ILogger<TenantArtifactCleanupService>? logger = null,
    IDiagnosticIdSource? diagnosticIds = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Guid? _cursor;

    public async ValueTask<int> RecoverAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var page = await store.ListDeletedArtifactOwnersAsync(_cursor, 100, ct).ConfigureAwait(false);
            var count = 0;
            foreach (var target in page.Items)
            {
                ct.ThrowIfCancellationRequested();
                try { if (await store.CleanupDeletedArtifactsAsync(target, ct).ConfigureAwait(false)) count++; }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    HandledFailureLog.Warning(logger, error, "artifacts.cleanup", "artifact_cleanup_failed",
                        new(SessionId: target.Owner.SessionId, AgentInstanceId: target.Owner.AgentInstanceId), diagnostics: diagnosticIds);
                }
            }
            // Tombstones are retained. Failed targets retry when the bounded scan wraps or after restart.
            _cursor = page.NextCursor;
            return count;
        }
        finally { _gate.Release(); }
    }
}
