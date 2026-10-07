using AgentCore.Application.Ports;

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
            catch (Exception)
            {
                // Avoid physical paths/provider contents in logs; the durable receipts remain retryable.
                logger.LogWarning("Deleted-instance workspace cleanup failed; it will be retried.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
