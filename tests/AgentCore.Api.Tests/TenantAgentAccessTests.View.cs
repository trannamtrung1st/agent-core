using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AgentCore.Api.Tests;

public sealed partial class TenantAgentAccessTests
{
    [Fact]
    public async Task Agent_access_HTTP_derives_human_identity_and_reads_exact_scopes_without_secret_metadata()
    {
        await using var factory = new AgentCoreApiFactory();
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
        var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) }));
        var id = agent.GetProperty("instance").GetProperty("instanceId").GetGuid(); var path = api + $"/agents/{id}/access";
        var response = await owner.GetAsync(path); Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        var view = await Json(response); Assert.Equal(id, view.GetProperty("instanceId").GetGuid()); Assert.True(view.GetProperty("canGrantChat").GetBoolean());
        Assert.DoesNotContain(view.GetProperty("permissions").EnumerateArray(), p => p.GetProperty("allowed").GetBoolean());
        Assert.Equal("agent", view.GetProperty("authorityScope").GetString());
        Assert.Equal("tenant", (await Json(await owner.GetAsync(path + "?authorityScope=tenant"))).GetProperty("authorityScope").GetString());
        var json = view.GetRawText(); foreach (var key in new[] { "passwordHash", "securityStamp", "loginKey", "owner@example.test" }) Assert.DoesNotContain(key, json);
        foreach (var query in new[] { "audiencePage=-1", "principalPage=100001", "q=" + new string('x', 201), "authorityScope=system" })
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync(path + "?" + query)).StatusCode);
        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        var other = (await Json(await owner.PostAsJsonAsync("/api/v2/system/tenants", new { name = "Other Access", slug = "other-access" }))).GetProperty("tenantId").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/v2/tenants/{other}/agents/{id}/access")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + $"/agents/{id}/lifecycle", new { lifecycle = 1, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 })).StatusCode);
        Assert.All((await Json(await owner.GetAsync(path))).GetProperty("audience").GetProperty("items").EnumerateArray(), item => Assert.False(item.GetProperty("effectiveChat").GetBoolean()));
    }
}
