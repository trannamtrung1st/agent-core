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
    private readonly Func<CancellationToken, Task>? _workflows;
    private readonly TimeProvider _time;
    private readonly ILogger<AgentRunHostedService> _logger;
    private readonly IDiagnosticIdSource _diagnostics;
    private readonly SchedulerReadiness? _readiness;

    public AgentRunHostedService(
        TimeProvider time,
        ILogger<AgentRunHostedService> logger,
        IDiagnosticIdSource diagnostics,
        AgentCore.Application.Security.TenantApprovalService approvals,
        AgentCore.Application.Security.TenantCredentialCheckService credentialChecks,
        AgentCore.Application.Security.TenantBrowserEffectService browserEffects,
        ITenantRunExecution tenantExecution, AgentCore.Application.Security.TenantWorkspaceMutationEffectService workspaceEffects,
        AgentCore.Application.Security.TenantAccountEffectService accountEffects, AgentCore.Application.Security.TenantHttpEffectService httpEffects,
        AgentCore.Api.Realtime.TenantSessionLiveDelivery delivery, ITenantTaskStore tasks, ITenantAgentMessageStore messages,
        AgentCore.Application.Workflows.TenantWorkflowExecutionService workflows, SchedulerReadiness readiness)
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
            cancellationToken => delivery.PublishTaskHintsAsync(tasks, messages, cancellationToken).AsTask(),
            cancellationToken => workflows.ReconcileAsync(4, cancellationToken).AsTask(), readiness)
    {
    }

    internal AgentRunHostedService(
        Func<CancellationToken, Task> pass,
        TimeProvider time,
        ILogger<AgentRunHostedService> logger,
        IDiagnosticIdSource diagnostics,
        Func<CancellationToken, Task>? hints = null, Func<CancellationToken, Task>? workflows = null, SchedulerReadiness? readiness = null)
    {
        _pass = pass; _hints = hints; _workflows = workflows;
        _time = time;
        _logger = logger;
        _diagnostics = diagnostics;
        _readiness = readiness;
    }

    internal IDiagnosticIdSource DiagnosticIds => _diagnostics;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var loops = new List<Task> { LoopAsync(RunPassAsync, stoppingToken) };
        if (_hints is not null) loops.Add(LoopAsync(RunHintPassAsync, stoppingToken));
        if (_workflows is not null) loops.Add(LoopAsync(RunWorkflowPassAsync, stoppingToken));
        return Task.WhenAll(loops);
    }

    private async Task RunWorkflowPassAsync(CancellationToken stoppingToken)
    {
        using var activity = RuntimeTelemetry.Activity.StartActivity("worker.pass");
        activity?.SetTag("worker", "workflows");
        try { await _workflows!(stoppingToken).ConfigureAwait(false); _readiness?.RecordWorker("workflows", true); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { _readiness?.RecordWorker("workflows", false); DiagnosticLog.Warning(_logger, exception, _diagnostics.NewId(), "Workflow recovery pass failed."); }
    }

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
        using var activity = RuntimeTelemetry.Activity.StartActivity("worker.pass");
        activity?.SetTag("worker", "task-hints");
        try { await _hints!(stoppingToken).ConfigureAwait(false); _readiness?.RecordWorker("task-hints", true); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        { _readiness?.RecordWorker("task-hints", false); DiagnosticLog.Warning(_logger, exception, _diagnostics.NewId(), "Task hint pass failed."); }
    }

    internal async Task RunPassAsync(CancellationToken stoppingToken)
    {
        using var activity = RuntimeTelemetry.Activity.StartActivity("worker.pass");
        activity?.SetTag("worker", "runs");
        try
        {
            await _pass(stoppingToken).ConfigureAwait(false);
            _readiness?.RecordWorker("runs", true);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _readiness?.RecordWorker("runs", false);
            DiagnosticLog.Warning(
                _logger,
                exception,
                _diagnostics.NewId(),
                "AgentRun pass failed.");
        }
    }
}
