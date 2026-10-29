using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.TestHost;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Application.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace AgentCore.Api.Tests;

public class AgentCoreApiFactory : WebApplicationFactory<Program>
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

    protected virtual bool EnableTenantRunExecution => false;
    private sealed class NoTenantExecution : ITenantRunExecution
    {
        public ValueTask<int> ExecuteRunnableAsync(int limit, CancellationToken ct = default) => ValueTask.FromResult(0);
    }
    protected virtual IReadOnlyDictionary<string, string?> ExtraConfiguration =>
        new Dictionary<string, string?>();

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
        var repo = FindRepoRoot();
        builder.UseContentRoot(Path.Combine(repo, "src", "AgentCore.Api"));
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            var values = new Dictionary<string, string?>
            {
                ["AgentCore:Profile"] = "Synthetic",
                ["Security:Http:AllowInsecureLocalHttp"] = "true",
                ["Security:EphemeralBootstrap:Login"] = "owner@example.test",
                ["Security:EphemeralBootstrap:DisplayName"] = "Synthetic test owner",
                ["Security:EphemeralBootstrap:Password"] = "explicit-synthetic-test-password",
                ["Security:EphemeralBootstrap:TenantName"] = "Synthetic test organization",
                ["Security:EphemeralBootstrap:TenantSlug"] = "synthetic-test",

                ["Browser:FixturePort"] = "0",
                ["Browser:Headless"] = "true",
                ["AgentCore:DetachGracePeriodSeconds"] = "0",
                ["AgentCore:AgentDirectory"] = Path.Combine(repo, "agents"),
                ["Providers:Speech:Recognition:Adapter"] = "Synthetic",
                ["Providers:Speech:Synthesis:Adapter"] = "Synthetic",
                ["Persistence:WorkspaceRoot"] = Path.Combine(Path.GetTempPath(), "agent-core-ws", Guid.NewGuid().ToString("N")),
                ["Persistence:TemplateRoot"] = Path.Combine(repo, "agents", "templates"),
                ["Persistence:ArtifactRoot"] = Path.Combine(Path.GetTempPath(), "agent-core-art", Guid.NewGuid().ToString("N"))
            };
            foreach (var pair in ExtraConfiguration)
            {
                values[pair.Key] = pair.Value;
            }

            config.AddInMemoryCollection(values);
        });
        // Minimal-host test configuration is applied after early application option binding.
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
            // Admission/approval fixtures control their Run claims. Execution fixtures opt into the production worker.
            if (!EnableTenantRunExecution) services.Replace(ServiceDescriptor.Singleton<ITenantRunExecution, NoTenantExecution>());
            var options = (PersistenceOptions)services.Single(d => d.ServiceType == typeof(PersistenceOptions)).ImplementationInstance!;
            options.WorkspaceRoot = Path.Combine(Path.GetTempPath(), "agent-core-api-isolated", Guid.NewGuid().ToString("N"), "workspaces");
            options.CredentialProtectionKeyRoot = Path.Combine(Path.GetTempPath(), "agent-core-api-isolated", Guid.NewGuid().ToString("N"), "credential-keys");
            if (ExtraConfiguration.TryGetValue("Persistence:WorkspaceRoot", out var root) && root is not null)
                options.WorkspaceRoot = root;
        });
        TestHttpDefaults.UseLoopbackCaller(builder);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AgentCore.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException();
    }
}
