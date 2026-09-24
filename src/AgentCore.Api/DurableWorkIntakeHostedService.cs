using AgentCore.Application.Work;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentCore.Api;

public sealed class DurableWorkIntakeHostedService(
    DurableWorkIntake intake,
    TimeProvider time,
    ILogger<DurableWorkIntakeHostedService> logger) : BackgroundService
{
    public static readonly TimeSpan Cadence = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var admitted = await intake.AcceptAwaitingAsync(stoppingToken).ConfigureAwait(false);
                if (admitted.Accepted > 0 || admitted.Existing > 0 || admitted.Skipped > 0)
                {
                    logger.LogInformation(
                        "Durable intake pass accepted {AcceptedCount} existing {ExistingCount} skipped {SkippedCount}.",
                        admitted.Accepted,
                        admitted.Existing,
                        admitted.Skipped);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    "Durable intake pass failed ({ExceptionType}).",
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
