using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Triggers;
using AgentCore.Application.Work;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentCore.Api;

public sealed class DurableWorkHostedService : BackgroundService
{
    public static readonly TimeSpan Cadence = TimeSpan.FromSeconds(1);

    private readonly Func<CancellationToken, Task> _pass;
    private readonly TimeProvider _time;
    private readonly ILogger<DurableWorkHostedService> _logger;
    private readonly IDiagnosticIdSource _diagnostics;

    public DurableWorkHostedService(
        DurableReminderExecutor work,
        TimeProvider time,
        ILogger<DurableWorkHostedService> logger,
        IDiagnosticIdSource diagnostics)
        : this(
            async cancellationToken =>
            {
                var now = time.GetUtcNow();
                var executed = await work.ExecuteDueAsync(now, TriggerScheduler.DefaultBatchSize, cancellationToken)
                    .ConfigureAwait(false);
                if (executed > 0)
                {
                    logger.LogInformation("Durable work pass executed {ExecutedCount} item(s).", executed);
                }
            },
            time,
            logger,
            diagnostics)
    {
    }

    internal DurableWorkHostedService(
        Func<CancellationToken, Task> pass,
        TimeProvider time,
        ILogger<DurableWorkHostedService> logger,
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
                "Durable work pass failed.");
        }
    }
}
