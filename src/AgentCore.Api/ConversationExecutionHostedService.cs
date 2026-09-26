using AgentCore.Application.Conversation;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentCore.Api;

public sealed class ConversationExecutionHostedService(
    ConversationExecutionCoordinator coordinator,
    TimeProvider time,
    ILogger<ConversationExecutionHostedService> logger) : BackgroundService
{
    public static readonly TimeSpan Cadence = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var executed = await coordinator
                    .ExecuteRunnableAsync(ConversationExecutionCoordinator.DefaultBatchSize, stoppingToken)
                    .ConfigureAwait(false);
                if (executed > 0)
                {
                    logger.LogInformation("Conversation execution pass dispatched {ExecutedCount} turn(s).", executed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Conversation execution pass failed ({ExceptionType}).",
                    exception.GetType().Name);
            }

            try
            {
                await Task.Delay(Cadence, time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
