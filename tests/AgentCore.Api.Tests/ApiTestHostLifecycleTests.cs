using System.Net;
using AgentCore.Application.Security;
using Elsa.Common.Multitenancy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgentCore.Api.Tests;

public sealed class ApiTestHostLifecycleTests
{
    [Fact]
    public async Task Concurrent_test_host_disposal_drains_real_Elsa_and_allows_a_fresh_host()
    {
        for (var iteration = 0; iteration < 2; iteration++)
        {
            var witness = new StopWitness();
            await using var factory = new LifecycleFactory(witness);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v2/auth/csrf")).StatusCode);
            var services = factory.Services;
            var stopped = services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopped;
            Assert.NotNull(await services.GetRequiredService<ITenantService>().FindAsync(Tenant.DefaultTenantId));
            var first = Task.Run(async () => await factory.DisposeAsync());
            try
            {
                await witness.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var issued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var second = Task.Run(async () => { var pending = factory.DisposeAsync(); issued.TrySetResult(); await pending; });
                await issued.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var hostStop = factory.StopHostAsync();
                Assert.False(first.IsCompleted); Assert.False(second.IsCompleted);
                Assert.False(hostStop.IsCompleted);
                witness.Release.TrySetResult();
                await Task.WhenAll(first, second, hostStop).WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally { witness.Release.TrySetResult(); }
            Assert.Equal(1, witness.Calls);
            Assert.True(stopped.IsCancellationRequested);
            Assert.Throws<ObjectDisposedException>(() => services.GetRequiredService<IdentityService>());
        }
    }

    private sealed class StopWitness
    {
        public int Calls;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ObservedTenants(IServiceScopeFactory scopes, ITenantScopeFactory tenants, TenantEventsManager events,
        ITenantAccessor accessor, StopWitness witness) : DefaultTenantService(scopes, tenants, events, accessor), ITenantService
    {
        async Task ITenantService.DeactivateTenantsAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref witness.Calls); witness.Entered.TrySetResult();
            await witness.Release.Task;
            await base.DeactivateTenantsAsync(ct);
        }
    }

    private sealed class LifecycleFactory(StopWitness witness) : AgentCoreApiFactory
    {
        private IHost? host;
        public Task StopHostAsync() => host!.StopAsync();
        protected override IHost CreateHost(IHostBuilder builder) => host = base.CreateHost(builder);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => services.Replace(ServiceDescriptor.Singleton<ITenantService>(provider =>
                ActivatorUtilities.CreateInstance<ObservedTenants>(provider, witness))));
        }
    }
}
