using AgentCore.Application.Email;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;

namespace AgentCore.Api;

public sealed class EmailWatchHostedService(EmailMonitoringService monitoring, TimeProvider time,
    ILogger<EmailWatchHostedService> logger, IDiagnosticIdSource diagnostics) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await monitoring.RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            { HandledFailureLog.Warning(logger, ex, "email.watch.cycle", "email_watch_cycle_failed", diagnostics: diagnostics); }
            try { await Task.Delay(TimeSpan.FromSeconds(15), time, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
