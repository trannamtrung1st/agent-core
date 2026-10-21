using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Conversation;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Api.Tests;

public sealed class RuntimeStateApiTests
{
    [Fact]
    public async Task A_noncooperative_provider_is_bounded_by_the_injected_clock_without_erasing_successful_counts()
    {
        var clock = new FakeTimeProvider();
        var pending = new HangingBrowser();
        await using var factory = new AgentCoreApiFactory().WithWebHostBuilder(builder => builder.ConfigureTestServices(services => {
            services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock);
            services.RemoveAll<IBrowserStateReader>(); services.AddSingleton<IBrowserStateReader>(pending);
        }));
        using var client = TestOwnerCapability.CreateOwnerClient(factory);
        var instance = await factory.Services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 31);
        var reading = factory.Services.GetRequiredService<RuntimeStateService>().ObserveAsync(instance.InstanceId).AsTask();
        await pending.Started.Task;
        clock.Advance(TimeSpan.FromSeconds(2));
        var state = await reading.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("partial", state.Availability); Assert.Equal(0, state.Sessions!.Open);
        Assert.Contains(state.Issues, i => i.Kind == "browser" && i.Code == "observation_timeout");
        Assert.Null(state.Resources.Single(r => r.Kind == "browser").OpenPages);
        Assert.Equal(clock.GetUtcNow().AddSeconds(-2), state.ObservedAtUtc);
    }

    private sealed class FailedBrowser(Action called) : IBrowserStateReader
    {
        public ValueTask<ResourceStateObservation> ObserveAsync(Guid trustedInstanceId, CancellationToken ct = default)
        { called(); throw new IOException("SECRET_PROVIDER_BODY"); }
    }
    private sealed class HangingBrowser : IBrowserStateReader
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<ResourceStateObservation> ObserveAsync(Guid trustedInstanceId, CancellationToken ct = default)
        {
            Started.TrySetResult();
            return new(new TaskCompletionSource<ResourceStateObservation>(TaskCreationOptions.RunContinuationsAsynchronously).Task);
        }
    }
}
