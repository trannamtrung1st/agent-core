using AgentCore.Application.Triggers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentCore.Api;

public sealed class TriggerSchedulerHostedService(
    TriggerScheduler scheduler,
    TriggerOccurrenceRouter router,
    TimeProvider time,
    ILogger<TriggerSchedulerHostedService> logger) : BackgroundService
{
    public static readonly TimeSpan Cadence = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await scheduler.RunOnceAsync(time.GetUtcNow(), stoppingToken).ConfigureAwait(false);
                await router.RouteOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    "Trigger scheduler pass failed ({ExceptionType}).",
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
