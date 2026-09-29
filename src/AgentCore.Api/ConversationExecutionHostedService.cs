using AgentCore.Application.Conversation;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentCore.Api;

public sealed class ConversationExecutionHostedService : BackgroundService
{
    public static readonly TimeSpan Cadence = TimeSpan.FromSeconds(1);

    private readonly Func<CancellationToken, Task> _pass;
    private readonly TimeProvider _time;
    private readonly ILogger<ConversationExecutionHostedService> _logger;
    private readonly IDiagnosticIdSource _diagnostics;

    public ConversationExecutionHostedService(
        ConversationExecutionCoordinator coordinator,
        TimeProvider time,
        ILogger<ConversationExecutionHostedService> logger,
        IDiagnosticIdSource diagnostics)
        : this(
            async cancellationToken =>
            {
                var executed = await coordinator
                    .ExecuteRunnableAsync(ConversationExecutionCoordinator.DefaultBatchSize, cancellationToken)
                    .ConfigureAwait(false);
                if (executed > 0)
                {
                    logger.LogInformation("Conversation execution pass dispatched {ExecutedCount} turn(s).", executed);
                }
            },
            time,
            logger,
            diagnostics)
    {
    }

    internal ConversationExecutionHostedService(
        Func<CancellationToken, Task> pass,
        TimeProvider time,
        ILogger<ConversationExecutionHostedService> logger,
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
                "Conversation execution pass failed.");
        }
    }
}
