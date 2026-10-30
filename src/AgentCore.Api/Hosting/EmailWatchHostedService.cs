using AgentCore.Application.Email;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;

namespace AgentCore.Api;

public sealed class EmailWatchHostedService(TenantEmailMonitoringService tenantMonitoring, TimeProvider time,
    ILogger<EmailWatchHostedService> logger, IDiagnosticIdSource diagnostics, SchedulerReadiness readiness) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using (var activity = RuntimeTelemetry.Activity.StartActivity("worker.pass"))
            {
                activity?.SetTag("worker", "email-watch");
                try { await tenantMonitoring.RunOnceAsync(stoppingToken); readiness.RecordWorker("email-watch", true); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                { readiness.RecordWorker("email-watch", false); HandledFailureLog.Warning(logger, ex, "email.watch.cycle", "email_watch_cycle_failed", diagnostics: diagnostics); }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(15), time, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
