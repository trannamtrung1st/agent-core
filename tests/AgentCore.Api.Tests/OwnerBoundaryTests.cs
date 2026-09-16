using System.Net;
using System.Net.Http.Json;
using AgentCore.Api.Http;
using AgentCore.Contracts.Http;
using Microsoft.AspNetCore.Http;

namespace AgentCore.Api.Tests;

public sealed class OwnerBoundaryTests : IClassFixture<AgentCoreApiFactory>
{
    private readonly AgentCoreApiFactory _factory;

    public OwnerBoundaryTests(AgentCoreApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void IsLoopback_returns_false_when_remote_ip_is_unknown()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = null;
        Assert.False(TrustedLocalCaller.IsLoopback(context));
    }

    [Fact]
    public async Task V1_session_endpoints_require_owner_capability()
    {
        var client = _factory.CreateClient();
        var create = await client.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest("examiner", 1, "text"));
        Assert.Equal(HttpStatusCode.Unauthorized, create.StatusCode);

        var sessionId = Guid.NewGuid();
        var get = await client.GetAsync($"/api/v1/sessions/{sessionId}");
        Assert.Equal(HttpStatusCode.Unauthorized, get.StatusCode);

        var history = await client.GetAsync($"/api/v1/sessions/{sessionId}/messages?after=0");
        Assert.Equal(HttpStatusCode.Unauthorized, history.StatusCode);

        var delete = await client.DeleteAsync($"/api/v1/sessions/{sessionId}");
        Assert.Equal(HttpStatusCode.Unauthorized, delete.StatusCode);
    }
}
