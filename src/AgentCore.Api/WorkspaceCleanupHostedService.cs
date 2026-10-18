using AgentCore.Application.Ports;
using AgentCore.Application.Observability;

namespace AgentCore.Api;

/// <summary>Committed deletion receipts survive the commit-to-filesystem cleanup gap.</summary>
public sealed class WorkspaceCleanupHostedService(
    IServiceProvider services, ILogger<WorkspaceCleanupHostedService> logger, IDiagnosticIdSource? diagnosticIds = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        do
        {
            try
            {
                await services.GetRequiredService<AgentCore.Application.Workspaces.TenantArtifactCleanupService>().RecoverAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                HandledFailureLog.Warning(logger, ex, "artifacts.cleanup", "artifact_cleanup_scan_failed", diagnostics: diagnosticIds);
            }
            try
            {
                // Resolve inside supervision so an unavailable cleanup dependency does not block host startup.
                await services.GetRequiredService<IAdminLifecycleDeletion>().RecoverWorkspaceCleanupAsync(stoppingToken);
                await services.GetRequiredService<AgentCore.Application.Workspaces.SessionWorkingCleanupService>().RecoverAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // Avoid physical paths/provider contents in logs; the durable receipts remain retryable.
                HandledFailureLog.Warning(logger, ex, "workspace.cleanup", "workspace_cleanup_failed", diagnostics: diagnosticIds);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
