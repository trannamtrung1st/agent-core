using AgentCore.Application.Ports;
using AgentCore.Application.Observability;

namespace AgentCore.Api;

/// <summary>Retained lifecycle/policy and deletion receipts survive the commit-to-filesystem cleanup gap.</summary>
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
                await services.GetRequiredService<AgentCore.Application.Security.TenantAgentDecommissionCleanup>().RecoverAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { HandledFailureLog.Warning(logger, ex, "agent.cleanup", "agent_decommission_scan_failed", diagnostics: diagnosticIds); }
            try
            {
                await services.GetRequiredService<AgentCore.Application.Security.TenantBrowserRetirementService>().RecoverAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { HandledFailureLog.Warning(logger, ex, "browser.cleanup", "browser_retirement_scan_failed", diagnostics: diagnosticIds); }
            try
            {
                await services.GetRequiredService<AgentCore.Application.Workspaces.TenantWorkingCleanupService>().RecoverAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { HandledFailureLog.Warning(logger, ex, "working.cleanup", "working_cleanup_scan_failed", diagnostics: diagnosticIds); }
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
                await services.GetRequiredService<AgentCore.Application.Workspaces.TenantAttachmentCleanupService>().RecoverAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { HandledFailureLog.Warning(logger, ex, "attachments.cleanup", "attachment_cleanup_scan_failed", diagnostics: diagnosticIds); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
