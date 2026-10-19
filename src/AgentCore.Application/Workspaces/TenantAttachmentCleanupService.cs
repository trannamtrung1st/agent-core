using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Workspaces;

public sealed class TenantAttachmentCleanupService(ITenantAttachmentCleanupStore store, ILogger<TenantAttachmentCleanupService>? logger = null, IDiagnosticIdSource? diagnostics = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _cursor;
    public async ValueTask<int> RecoverAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var page = await store.ListAttachmentCleanupAsync(_cursor, 100, ct).ConfigureAwait(false); var count = 0;
            foreach (var target in page.Items)
            {
                ct.ThrowIfCancellationRequested();
                try { if (await store.CleanupAttachmentContentAsync(target, ct).ConfigureAwait(false)) count++; }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception error) { HandledFailureLog.Warning(logger, error, "attachments.cleanup", "attachment_cleanup_failed", new(SessionId: target.SessionId), diagnostics: diagnostics); }
            }
            _cursor = page.NextCursor; return count;
        }
        finally { _gate.Release(); }
    }
}
