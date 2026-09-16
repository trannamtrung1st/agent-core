using AgentCore.Application.Ports;

namespace AgentCore.Api;

public sealed class AttachmentTtlHostedService(IAttachmentStore store) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await store.SweepExpiredAsync(stoppingToken).ConfigureAwait(false);
        }
    }
}
