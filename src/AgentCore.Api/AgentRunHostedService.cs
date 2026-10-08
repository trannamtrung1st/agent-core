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
    private readonly TimeProvider _time;
    private readonly ILogger<AgentRunHostedService> _logger;
    private readonly IDiagnosticIdSource _diagnostics;

    public AgentRunHostedService(
        AgentRunCoordinator coordinator,
        TimeProvider time,
        ILogger<AgentRunHostedService> logger,
        IDiagnosticIdSource diagnostics,
        AgentCore.Application.Experience.ExperienceService experience)
        : this(
            async cancellationToken =>
            {
                await experience.ReconcileAsync(cancellationToken).ConfigureAwait(false);
                var executed = await coordinator
                    .ExecuteRunnableAsync(AgentRunCoordinator.DefaultBatchSize, cancellationToken)
                    .ConfigureAwait(false);
                if (executed > 0)
                {
                    logger.LogInformation("AgentRun pass dispatched {ExecutedCount} turn(s).", executed);
                }
            },
            time,
            logger,
            diagnostics)
    {
    }

    internal AgentRunHostedService(
        Func<CancellationToken, Task> pass,
        TimeProvider time,
        ILogger<AgentRunHostedService> logger,
        IDiagnosticIdSource diagnostics)
    {
        _pass = pass;
        _time = time;
        _logger = logger;
        _diagnostics = diagnostics;
    }

    internal IDiagnosticIdSource DiagnosticIds => _diagnostics;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunPassAsync(stoppingToken).ConfigureAwait(false);
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
