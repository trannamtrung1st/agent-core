using AgentCore.Application.Execution;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentCore.Api;

public sealed class AgentRunHostedService : BackgroundService
{
    public static readonly TimeSpan Cadence = TimeSpan.FromSeconds(1);

    private readonly Func<CancellationToken, Task> _pass;
    private readonly Func<CancellationToken, Task>? _hints;
    private readonly TimeProvider _time;
    private readonly ILogger<AgentRunHostedService> _logger;
    private readonly IDiagnosticIdSource _diagnostics;

    public AgentRunHostedService(
        TimeProvider time,
        ILogger<AgentRunHostedService> logger,
        IDiagnosticIdSource diagnostics,
        AgentCore.Application.Security.TenantApprovalService approvals,
        AgentCore.Application.Security.TenantCredentialCheckService credentialChecks,
        AgentCore.Application.Security.TenantBrowserEffectService browserEffects,
        ITenantRunExecution tenantExecution, AgentCore.Application.Security.TenantWorkspaceMutationEffectService workspaceEffects,
        AgentCore.Application.Security.TenantAccountEffectService accountEffects, AgentCore.Application.Security.TenantHttpEffectService httpEffects,
        AgentCore.Api.Realtime.TenantSessionLiveDelivery delivery, ITenantTaskStore tasks, ITenantAgentMessageStore messages)
        : this(
            async cancellationToken =>
            {
                await approvals.DispatchPendingAsync(cancellationToken).ConfigureAwait(false);
                await credentialChecks.RecoverAsync(cancellationToken).ConfigureAwait(false);
                await browserEffects.RecoverAsync(cancellationToken).ConfigureAwait(false);
                await workspaceEffects.RecoverAsync(cancellationToken).ConfigureAwait(false);
                await accountEffects.RecoverAsync(cancellationToken).ConfigureAwait(false);
                await httpEffects.RecoverAsync(cancellationToken).ConfigureAwait(false);
                var executed = await tenantExecution.ExecuteRunnableAsync(AgentRunCoordinator.DefaultBatchSize, cancellationToken).ConfigureAwait(false);
                if (executed > 0)
                {
                    logger.LogInformation("AgentRun pass dispatched {ExecutedCount} turn(s).", executed);
                }
            },
            time,
            logger,
            diagnostics,
            cancellationToken => delivery.PublishTaskHintsAsync(tasks, messages, cancellationToken).AsTask())
    {
    }

    internal AgentRunHostedService(
        Func<CancellationToken, Task> pass,
        TimeProvider time,
        ILogger<AgentRunHostedService> logger,
        IDiagnosticIdSource diagnostics,
        Func<CancellationToken, Task>? hints = null)
    {
        _pass = pass; _hints = hints;
        _time = time;
        _logger = logger;
        _diagnostics = diagnostics;
    }

    internal IDiagnosticIdSource DiagnosticIds => _diagnostics;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => _hints is null
        ? LoopAsync(RunPassAsync, stoppingToken)
        : Task.WhenAll(LoopAsync(RunPassAsync, stoppingToken), LoopAsync(RunHintPassAsync, stoppingToken));

    // Separately awaited loops in the existing host: slow hint reads never hold up
    // Run dispatch, and shutdown still cancels and drains both owned operations.
    private async Task LoopAsync(Func<CancellationToken, Task> pass, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await pass(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await Task.Delay(Cadence, _time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RunHintPassAsync(CancellationToken stoppingToken)
    {
        try { await _hints!(stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        { DiagnosticLog.Warning(_logger, exception, _diagnostics.NewId(), "Task hint pass failed."); }
    }

    internal async Task RunPassAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _pass(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            DiagnosticLog.Warning(
                _logger,
                exception,
                _diagnostics.NewId(),
                "AgentRun pass failed.");
        }
    }
}
