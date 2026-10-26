using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Sandbox;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Fact]
    public async Task Tenant_Sandbox_HTTP_returns_scoped_stored_observations_and_rejects_foreign_or_legacy_authority()
    {
        await using var factory = new AgentCoreApiFactory(); using var owner = Client(factory);
        Assert.Null(factory.Services.GetService<IAgentRunAuthority>());
        Assert.Null(typeof(IAgentRunAuthority).Assembly.GetType("AgentCore.Application.Execution.AgentRunAuthority"));
        Assert.Null(typeof(ISandboxService).Assembly.GetType("AgentCore.Application.Ports.SandboxAccess"));
        foreach (var retired in new[] { "ExecuteAsync", "InspectAsync", "InspectProcessAsync", "StopProcessAsync" })
            Assert.Null(typeof(OpenSandboxService).GetMethod(retired));
        Assert.DoesNotContain(typeof(OpenSandboxService).GetConstructors().Single().GetParameters(), p =>
            p.ParameterType == typeof(IAgentRunStore) || p.ParameterType == typeof(IAgentRunAuthority)
            || p.ParameterType == typeof(IAgentInstanceStore) || p.ParameterType == typeof(IAgentDefinitionStore));
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var api = $"/api/v2/tenants/{tenant}";
        var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) }));
        var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid();
        var session = await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }));
        var id = session.GetProperty("sessionId").GetGuid(); var path = api + $"/sessions/{id}/sandbox";
        var response = await owner.GetAsync(path); Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        var observed = await Json(response); Assert.Equal(id, observed.GetProperty("sessionId").GetGuid());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, observed.GetProperty("environment").ValueKind);
        Assert.False(observed.GetProperty("liveAuthorityConfirmed").GetBoolean());
        var controlsResponse = await owner.GetAsync(path + "/controls"); Assert.Equal(HttpStatusCode.OK, controlsResponse.StatusCode);
        Assert.True(controlsResponse.Headers.CacheControl?.NoStore); var controls = await Json(controlsResponse);
        Assert.Equal(id, controls.GetProperty("observation").GetProperty("sessionId").GetGuid());
        Assert.False(controls.GetProperty("canReset").GetBoolean()); Assert.False(controls.GetProperty("canStop").GetBoolean());
        Assert.Equal(1, controls.GetProperty("instanceRevision").GetInt64());
        Assert.Empty(controls.GetProperty("profiles").EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync(path + $"/executions/{Guid.NewGuid()}")).StatusCode);
        using var anonymous = Client(factory); using var legacy = RetiredOwnerHeader.CreateClient(factory);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await legacy.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path + "/controls")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await legacy.GetAsync(path + "/controls")).StatusCode);
        var beta = (await Json(await owner.PostAsJsonAsync("/api/v2/system/tenants", new { name = "Other Sandbox", slug = "other-sandbox" }))).GetProperty("tenantId").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/v2/tenants/{beta}/sessions/{id}/sandbox")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/v2/tenants/{beta}/sessions/{id}/sandbox/controls")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await owner.PostAsJsonAsync(path, new { command = "echo no dispatch" })).StatusCode);
    }
}
