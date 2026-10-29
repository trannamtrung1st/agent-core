using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgentCore.Api.Tests;

// Both memory and SQLite fixture roots retain the same real host lifecycle.
public abstract class AgentCoreTestHostFactory : WebApplicationFactory<Program>
{
    private readonly object disposalGate = new();
    private Task? disposal;

    public override ValueTask DisposeAsync()
    {
        lock (disposalGate) return new(disposal ??= base.DisposeAsync().AsTask());
    }

    private sealed class SingleStopHostedService(IHostedService inner) : IHostedService
    {
        private readonly object stopGate = new();
        private Task? stopping;
        public Task StartAsync(CancellationToken ct) => inner.StartAsync(ct);
        public Task StopAsync(CancellationToken ct)
        {
            lock (stopGate) return stopping ??= StopOnceAsync(ct);
        }
        private async Task StopOnceAsync(CancellationToken ct) => await inner.StopAsync(ct).ConfigureAwait(false);
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);
        // Minimal-host test teardown can race the entry point's own disposal.
        // Resolve the lazy ingress snapshot while DI is live; Elsa's real drain
        // still runs at shutdown without first resolving sources from a retired provider.
        _ = host.Services.GetRequiredService<Elsa.Workflows.Runtime.IIngressSourceRegistry>().Snapshot();
        return host;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            // Minimal-host entry-point shutdown and WebApplicationFactory can both
            // stop this host. Keep the SDK's real stop task single-flight. Keyed
            // registration preserves native disposal ownership and service order.
            foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IHostedService) && !d.IsKeyedService
                && (d.ImplementationType == typeof(Elsa.Common.Multitenancy.HostedServices.ActivateTenants)
                    || d.ImplementationType == typeof(Elsa.Workflows.Runtime.HostedServices.DrainOrchestratorHostedService))).ToArray())
            {
                var key = new object();
                var index = services.IndexOf(descriptor);
                var implementation = descriptor.ImplementationType!;
                services[index] = ServiceDescriptor.Singleton<IHostedService>(provider =>
                    new SingleStopHostedService((IHostedService)provider.GetRequiredKeyedService(implementation, key)));
                services.Add(ServiceDescriptor.DescribeKeyed(implementation, key, implementation, descriptor.Lifetime));
            }
        });
    }
}
