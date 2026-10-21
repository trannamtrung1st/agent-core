using AgentCore.Api.Realtime;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Triggers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AgentCore.Api.Tests;

public sealed class TenantRuntimeRegistryTests
{
    [Fact]
    public async Task Host_resolves_one_canonical_runtime_owner_without_an_unowned_dispatch_or_occurrence_registry()
    {
        await using var factory = new AgentCoreApiFactory();
        var services = factory.Services;
        var runtime = services.GetRequiredService<TenantSessionRuntimeHost>();
        Assert.Same(runtime, services.GetRequiredService<ITenantRunDispatcher>());
        Assert.Same(runtime, services.GetRequiredService<ITenantRunControl>());
        Assert.Same(runtime, services.GetRequiredService<ITenantSessionInputControl>());
        Assert.Same(runtime, services.GetRequiredService<ITenantSessionForegroundControl>());
        Assert.Same(runtime, services.GetRequiredService<ITenantDeliveryReceiptControl>());
        Assert.Null(typeof(SessionHub).Assembly.GetType("AgentCore.Api.Realtime.SessionHost"));
        Assert.Null(services.GetService<IAgentRunDispatcher>());
        Assert.Null(services.GetService<AgentRunCoordinator>());
        Assert.Null(services.GetService<BackgroundCompletionReporter>());
        Assert.Null(services.GetService<BackgroundOccurrenceIntake>());
        Assert.Null(services.GetService<ILiveOccurrenceDirectory>());
        Assert.Null(services.GetService<IOccurrenceMailbox>());
        Assert.Null(services.GetService<IEnvironmentEventIngress>());
        Assert.Null(services.GetService<IHostedSessionStateReader>());
        var hosted = services.GetServices<IHostedService>().Select(s => s.GetType().Name).ToArray();
        Assert.DoesNotContain("BackgroundOccurrenceIntakeHostedService", hosted);
        Assert.DoesNotContain("AttachmentTtlHostedService", hosted);
        Assert.Contains(nameof(AgentRunHostedService), hosted);
        Assert.Contains(nameof(TriggerSchedulerHostedService), hosted);
        Assert.Contains(nameof(EmailWatchHostedService), hosted);
    }

    [Fact]
    public async Task Shared_capacity_is_bounded_and_shutdown_is_idempotent_and_never_reopens_admission()
    {
        await using var factory = new AgentCoreApiFactory();
        var capacity = factory.Services.GetRequiredService<TenantRuntimeCapacity>();
        var slots = factory.Services.GetRequiredService<IOptions<AgentCoreOptions>>().Value.MaxActiveSessions;
        for (var i = 0; i < slots; i++) Assert.True(capacity.TryReserveTenantRuntime());
        Assert.False(capacity.TryReserveTenantRuntime());
        capacity.ReleaseTenantRuntime(); Assert.True(capacity.TryReserveTenantRuntime());
        var runtime = factory.Services.GetRequiredService<TenantSessionRuntimeHost>();
        await Task.WhenAll(runtime.StopAsync(), runtime.StopAsync());
        for (var i = 0; i <= slots; i++) capacity.ReleaseTenantRuntime();
        Assert.False(capacity.TryReserveTenantRuntime());
        await runtime.DisposeAsync();
        Assert.False(capacity.TryReserveTenantRuntime());
    }
}
