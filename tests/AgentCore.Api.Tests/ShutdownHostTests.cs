using System.Net;
using System.Net.Http.Json;
using AgentCore.Api.Realtime;
using AgentCore.Contracts.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed class ShutdownHostTests
{
    [Fact]
    public async Task Shutdown_stops_admitting_new_sessions()
    {
        await using var factory = new AgentCoreApiFactory();
        var host = factory.Services.GetRequiredService<SessionHost>();
        Assert.True(host.Admitting);
        await host.DrainAsync();
        var client = factory.CreateClient();
        var created = await client.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest("examiner", 1, "text"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, created.StatusCode);
        Assert.False(host.Admitting);
    }
}
