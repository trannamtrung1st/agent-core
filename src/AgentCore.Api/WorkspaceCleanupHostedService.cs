using AgentCore.Application.Ports;
using AgentCore.Application.Observability;

namespace AgentCore.Api;

/// <summary>Committed deletion receipts survive the commit-to-filesystem cleanup gap.</summary>
public sealed class WorkspaceCleanupHostedService(
    IServiceProvider services, ILogger<WorkspaceCleanupHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        do
        {
            try
            {
                // Resolve inside supervision so an unavailable cleanup dependency does not block host startup.
                await services.GetRequiredService<IAdminLifecycleDeletion>().RecoverWorkspaceCleanupAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // Avoid physical paths/provider contents in logs; the durable receipts remain retryable.
                HandledFailureLog.Warning(logger, ex, "workspace.cleanup", "workspace_cleanup_failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
