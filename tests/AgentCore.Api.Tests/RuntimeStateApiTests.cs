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

    [Fact]
    public async Task Owner_read_counts_durable_sessions_without_hosting_them_and_redacts_provider_failures()
    {
        var calls = 0;
        await using var factory = new AgentCoreApiFactory().WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IBrowserStateReader>();
            services.AddSingleton<IBrowserStateReader>(new FailedBrowser(() => calls++));
        }));
        using var anonymous = factory.CreateClient();
        using var client = TestOwnerCapability.CreateOwnerClient(factory);
        var instance = await factory.Services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 31);
        var other = await factory.Services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 31);
        var manager = factory.Services.GetRequiredService<SessionManager>();
        var ownSession = await manager.CreateForInstanceAsync(instance.InstanceId, SessionMode.Text);
        await manager.CreateForInstanceAsync(instance.InstanceId, SessionMode.Text);
        await manager.CreateForInstanceAsync(other.InstanceId, SessionMode.Text);
        var path = $"/api/v2/admin/agent-instances/{instance.InstanceId}/state";
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        Assert.Equal(0, calls);
        var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        var raw = await response.Content.ReadAsStringAsync();
        var state = (await response.Content.ReadFromJsonAsync<RuntimeStateResponse>())!;
        Assert.Equal("partial", state.Availability);
        Assert.Equal(2, state.Sessions!.Open);
        Assert.Equal(0, state.Sessions.Paused);
        Assert.Equal(0, state.HostedSessions!.Runtimes);
        Assert.Equal(0, state.HostedSessions.Attached);
        Assert.Equal(0, state.Runs!.Executing);
        Assert.Equal(0, state.Runs.Terminal);
        Assert.Equal(1, calls);
        var scoped = await factory.Services.GetRequiredService<RuntimeStateService>().ObserveOwnedSessionAsync(instance.InstanceId, ownSession.SessionId);
        Assert.Equal("currentSession", scoped.Scope);
        Assert.Equal(1, scoped.Sessions!.Open);
        await Assert.ThrowsAsync<AgentCoreException>(async () => await factory.Services.GetRequiredService<RuntimeStateService>()
            .ObserveOwnedSessionAsync(other.InstanceId, ownSession.SessionId));
        Assert.Null(state.Resources.Single(r => r.Kind == "browser").OpenPages);
        Assert.Equal(2, state.Resources.Count);
        Assert.Contains(state.Issues, i => i.Kind == "browser" && i.Code == "observation_unavailable");
        Assert.Equal(TimeSpan.FromSeconds(15), state.FreshThroughUtc - state.ObservedAtUtc);
        Assert.DoesNotContain("SECRET_PROVIDER_BODY", raw);
        Assert.DoesNotContain(other.InstanceId.ToString(), raw);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v2/admin/agent-instances/{Guid.NewGuid()}/state")).StatusCode);
        Assert.Equal(1, calls);
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
