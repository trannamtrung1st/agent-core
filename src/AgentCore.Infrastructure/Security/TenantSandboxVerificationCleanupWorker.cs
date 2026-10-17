using AgentCore.Application.Security;
using Microsoft.Extensions.Hosting;

namespace AgentCore.Infrastructure.Security;

// Independent of catalog eligibility/readiness: only saved technical cleanup claims are exposed.
public sealed class TenantSandboxVerificationCleanupWorker(TenantSandboxVerificationCleanup cleanup, TimeProvider time) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15), time);
        do
        {
            try
            {
                using var pass = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken); pass.CancelAfter(TimeSpan.FromSeconds(30));
                await cleanup.RunPassAsync(pass.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch { /* Exact receipts remain pending; never repeat initialization or release unknown files. */ }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
