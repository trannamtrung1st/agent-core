using AgentCore.Api.Realtime;

namespace AgentCore.Api.Realtime;

public sealed class SessionShutdownHostedService(SessionHost host) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => host.DrainAsync(cancellationToken);
}
