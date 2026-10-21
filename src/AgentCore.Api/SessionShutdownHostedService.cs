namespace AgentCore.Api.Realtime;

public sealed class SessionShutdownHostedService(TenantSessionRuntimeHost host) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => host.StopAsync(cancellationToken);
}
