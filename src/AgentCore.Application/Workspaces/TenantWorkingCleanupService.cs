using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Workspaces;

public sealed class TenantWorkingCleanupService(ITenantWorkingCleanupStore store, ITenantWorkingCleanupBytes bytes,
    ILogger<TenantWorkingCleanupService>? logger = null, IDiagnosticIdSource? diagnosticIds = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Guid? _cursor;

    public async ValueTask<bool> CleanupAsync(TenantSessionRoot owner, CancellationToken ct = default)
    {
        var target = await store.ReadTenantWorkingCleanupAsync(owner, ct).ConfigureAwait(false);
        return target is not null && await bytes.PurgeTenantWorkingAsync(target,
            async token => await store.ReadTenantWorkingCleanupAsync(owner, token).ConfigureAwait(false) == target, ct).ConfigureAwait(false);
    }

    public async ValueTask<int> RecoverAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var page = await store.ListTenantWorkingCleanupAsync(_cursor, 100, ct).ConfigureAwait(false);
            var count = 0;
            foreach (var owner in page.Items)
            {
                ct.ThrowIfCancellationRequested();
                try { if (await CleanupAsync(owner, ct).ConfigureAwait(false)) count++; }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    HandledFailureLog.Warning(logger, error, "working.cleanup", "working_cleanup_failed",
                        new(SessionId: owner.SessionId, AgentInstanceId: owner.AgentInstanceId), diagnostics: diagnosticIds);
                }
            }
            // Retained terminal owners and tombstones retry after a bounded wrap or process restart.
            _cursor = page.NextCursor;
            return count;
        }
        finally { _gate.Release(); }
    }
}
