using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Execution;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentCore.Api;

public sealed class BackgroundOccurrenceIntakeHostedService : BackgroundService
{
    public static readonly TimeSpan Cadence = TimeSpan.FromSeconds(1);

    private readonly Func<CancellationToken, Task> _pass;
    private readonly TimeProvider _time;
    private readonly ILogger<BackgroundOccurrenceIntakeHostedService> _logger;
    private readonly IDiagnosticIdSource _diagnostics;

    public BackgroundOccurrenceIntakeHostedService(
        BackgroundOccurrenceIntake intake,
        TimeProvider time,
        ILogger<BackgroundOccurrenceIntakeHostedService> logger,
        IDiagnosticIdSource diagnostics)
        : this(
            async cancellationToken =>
            {
                var admitted = await intake.AcceptAwaitingAsync(cancellationToken).ConfigureAwait(false);
                if (admitted.Accepted > 0 || admitted.Existing > 0 || admitted.Skipped > 0)
                {
                    logger.LogInformation(
                        "Background admission pass accepted {AcceptedCount} existing {ExistingCount} skipped {SkippedCount}.",
                        admitted.Accepted,
                        admitted.Existing,
                        admitted.Skipped);
                }
            },
            time,
            logger,
            diagnostics)
    {
    }

    internal BackgroundOccurrenceIntakeHostedService(
        Func<CancellationToken, Task> pass,
        TimeProvider time,
        ILogger<BackgroundOccurrenceIntakeHostedService> logger,
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
                "Background admission pass failed.");
        }
    }
}
