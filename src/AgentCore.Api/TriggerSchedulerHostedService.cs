using AgentCore.Application.Triggers;
using AgentCore.Application.Work;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentCore.Api;

public sealed class TriggerSchedulerHostedService(
    TriggerScheduler scheduler,
    TriggerOccurrenceRouter router,
    DurableWorkIntake intake,
    DurableReminderExecutor work,
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
                var now = time.GetUtcNow();
                await scheduler.RunOnceAsync(now, stoppingToken).ConfigureAwait(false);
                await router.RouteOnceAsync(stoppingToken).ConfigureAwait(false);
                var admitted = await intake.AcceptAwaitingAsync(stoppingToken).ConfigureAwait(false);
                var executed = await work.ExecuteDueAsync(now, TriggerScheduler.DefaultBatchSize, stoppingToken)
                    .ConfigureAwait(false);
                if (admitted.Accepted > 0 || admitted.Existing > 0 || executed > 0)
                {
                    logger.LogInformation(
                        "Durable work pass accepted {AcceptedCount} existing {ExistingCount} executed {ExecutedCount}.",
                        admitted.Accepted,
                        admitted.Existing,
                        executed);
                }
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
