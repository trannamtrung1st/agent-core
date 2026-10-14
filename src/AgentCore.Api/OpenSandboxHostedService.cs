using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Sandbox;

namespace AgentCore.Api;

public sealed class OpenSandboxHostedService(OpenSandboxReadiness readiness, ISandboxService sandboxes) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        do
        {
            // Cleanup remains active when every profile is disabled or readiness fails.
            try
            {
                using var pass = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                pass.CancelAfter(TimeSpan.FromSeconds(30));
                await readiness.RefreshAsync(pass.Token); await sandboxes.ReconcileAsync(pass.Token);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { /* Retry remaining durable cleanup next pass. */ }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch { /* Durable receipts survive outages; safe status is owned by readiness. */ }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
