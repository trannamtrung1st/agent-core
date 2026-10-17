using System.Net;
using System.Net.Http.Json;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Fact]
    public async Task Tenant_Sandbox_HTTP_returns_scoped_stored_observations_and_rejects_foreign_or_legacy_authority()
    {
        await using var factory = new AgentCoreApiFactory(); using var owner = Client(factory);
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var api = $"/api/v2/tenants/{tenant}";
        var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) }));
        var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid();
        var session = await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }));
        var id = session.GetProperty("sessionId").GetGuid(); var path = api + $"/sessions/{id}/sandbox";
        var response = await owner.GetAsync(path); Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        var observed = await Json(response); Assert.Equal(id, observed.GetProperty("sessionId").GetGuid());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, observed.GetProperty("environment").ValueKind);
        Assert.False(observed.GetProperty("liveAuthorityConfirmed").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync(path + $"/executions/{Guid.NewGuid()}")).StatusCode);
        using var anonymous = Client(factory); using var legacy = TestOwnerCapability.CreateOwnerClient(factory);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await legacy.GetAsync(path)).StatusCode);
        var beta = (await Json(await owner.PostAsJsonAsync("/api/v2/system/tenants", new { name = "Other Sandbox", slug = "other-sandbox" }))).GetProperty("tenantId").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/v2/tenants/{beta}/sessions/{id}/sandbox")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await owner.PostAsJsonAsync(path, new { command = "echo no dispatch" })).StatusCode);
    }
}
