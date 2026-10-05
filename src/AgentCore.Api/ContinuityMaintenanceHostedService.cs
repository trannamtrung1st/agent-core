using AgentCore.Application.Continuity;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;

namespace AgentCore.Api;

public sealed class ContinuityMaintenanceHostedService(ContinuityMaintenance maintenance, TimeProvider time,
    ILogger<ContinuityMaintenanceHostedService> logger, IDiagnosticIdSource diagnostics) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await maintenance.RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { DiagnosticLog.Warning(logger, ex, diagnostics.NewId(), "Continuity maintenance failed."); }
            try { await Task.Delay(ContinuityMaintenance.Cadence, time, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
