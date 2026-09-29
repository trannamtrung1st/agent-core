using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Triggers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentCore.Api;

public sealed class TriggerSchedulerHostedService : BackgroundService
{
    public static readonly TimeSpan Cadence = TimeSpan.FromSeconds(1);

    private readonly Func<CancellationToken, Task> _pass;
    private readonly TimeProvider _time;
    private readonly ILogger<TriggerSchedulerHostedService> _logger;
    private readonly IDiagnosticIdSource _diagnostics;

    public TriggerSchedulerHostedService(
        TriggerScheduler scheduler,
        TriggerOccurrenceRouter router,
        TimeProvider time,
        ILogger<TriggerSchedulerHostedService> logger,
        IDiagnosticIdSource diagnostics)
        : this(
            cancellationToken => RunProductionAsync(scheduler, router, time, cancellationToken),
            time,
            logger,
            diagnostics)
    {
    }

    internal TriggerSchedulerHostedService(
        Func<CancellationToken, Task> pass,
        TimeProvider time,
        ILogger<TriggerSchedulerHostedService> logger,
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
                "Trigger scheduler pass failed.");
        }
    }

    private static async Task RunProductionAsync(
        TriggerScheduler scheduler,
        TriggerOccurrenceRouter router,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await scheduler.RunOnceAsync(now, cancellationToken).ConfigureAwait(false);
        await router.RouteOnceAsync(cancellationToken).ConfigureAwait(false);
    }
}
