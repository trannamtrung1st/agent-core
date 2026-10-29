using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Fact]
    public async Task Canonical_Session_HTTP_pins_authenticated_creator_and_actual_executor_and_excludes_legacy_routes()
    {
        await using var factory = new AgentCoreApiFactory(); using var owner = Client(factory);
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var api = $"/api/v2/tenants/{tenant}";
        var creator = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
        var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) }));
        var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid(); var executor = agent.GetProperty("principalId").GetGuid();
        var root = api + "/sessions";
        owner.DefaultRequestHeaders.Remove("X-AgentCore-CSRF");
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(root, new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 })).StatusCode);
        await Csrf(owner);
        var response = await owner.PostAsJsonAsync(root, new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        var created = await Json(response); var id = created.GetProperty("sessionId").GetGuid();
        Assert.Equal(tenant, created.GetProperty("tenantId").GetGuid()); Assert.Equal(executor, created.GetProperty("executorPrincipalId").GetGuid());
        Assert.Equal(creator, created.GetProperty("createdByPrincipalId").GetGuid()); Assert.Equal("text", created.GetProperty("mode").GetString());
        Assert.Equal("created", created.GetProperty("status").GetString());
        foreach (var field in new[] { "definition", "profileId", "ownerCapability", "agentInstructions", "entries", "summary" }) Assert.False(created.TryGetProperty(field, out _));
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync(root + $"/{id}")).StatusCode);
        Assert.Equal(1, (await Json(await owner.GetAsync(root))).GetProperty("total").GetInt32());
        using var anonymous = Client(factory); using var legacy = RetiredOwnerHeader.CreateClient(factory);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(root + $"/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await legacy.GetAsync(root + $"/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await legacy.GetAsync($"/api/v2/host/sessions/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await legacy.GetAsync($"/api/v1/sessions/{id}")).StatusCode);
        var beta = (await Json(await owner.PostAsJsonAsync("/api/v2/system/tenants", new { name = "Beta Sessions", slug = "beta-sessions" }))).GetProperty("tenantId").GetGuid();
        Assert.Equal(0, (await Json(await owner.GetAsync($"/api/v2/tenants/{beta}/sessions"))).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/v2/tenants/{beta}/sessions/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync($"/api/v2/tenants/{beta}/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, beta), expectedInstanceRevision = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync(root, new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = 1, expectedInstanceRevision = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(root, new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1, mode = "invalid" })).StatusCode);
        Assert.Equal(1, (await Json(await owner.GetAsync(root))).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PatchAsJsonAsync($"/api/v2/tenants/{beta}/sessions/{id}", new { title = "Foreign", expectedRevision = 1, expectedSessionRevision = 1 })).StatusCode);
        var renamed = await owner.PatchAsJsonAsync(root + $"/{id}", new { title = "  Session review  ", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode); Assert.True(renamed.Headers.CacheControl?.NoStore);
        var current = await Json(renamed); Assert.Equal("Session review", current.GetProperty("title").GetString());
        foreach (var action in new[] { "pause", "resume", "archive", "restore", "complete" })
        {
            var changed = await owner.PostAsJsonAsync(root + $"/{id}/{action}", new { expectedRevision = await Revision(owner, tenant), expectedSessionRevision = current.GetProperty("revision").GetInt64(), source = "system" });
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode); current = await Json(changed);
            Assert.False(current.TryGetProperty("definition", out _)); Assert.False(current.TryGetProperty("summary", out _));
            if (action == "pause") Assert.Equal("paused", current.GetProperty("lifecycleStatus").GetString());
            if (action == "archive") Assert.NotEqual(JsonValueKind.Null, current.GetProperty("archivedAt").ValueKind);
            if (action == "restore") Assert.Equal(JsonValueKind.Null, current.GetProperty("archivedAt").ValueKind);
        }
        Assert.Equal("completed", current.GetProperty("lifecycleStatus").GetString());
        var stored = await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(tenant, id);
        Assert.Equal(LifecycleTransitionSource.User, stored!.Snapshot.LifecycleSource);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(root + $"/{id}/resume", new { expectedRevision = await Revision(owner, tenant), expectedSessionRevision = current.GetProperty("revision").GetInt64() })).StatusCode);
        var deletion = root + $"/{id}?expectedRevision={await Revision(owner, tenant)}&expectedSessionRevision={current.GetProperty("revision").GetInt64()}";
        owner.DefaultRequestHeaders.Remove("X-AgentCore-CSRF"); Assert.Equal(HttpStatusCode.BadRequest, (await owner.DeleteAsync(deletion)).StatusCode); await Csrf(owner);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync(deletion)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync(root + $"/{id}")).StatusCode);
        Assert.Equal(0, (await Json(await owner.GetAsync(root))).GetProperty("total").GetInt32());
        var another = await Json(await owner.PostAsJsonAsync(root, new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }));
        var cancelled = await owner.PostAsJsonAsync(root + $"/{another.GetProperty("sessionId").GetGuid()}/cancel", new { expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 });
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode); Assert.Equal("cancelled", (await Json(cancelled)).GetProperty("lifecycleStatus").GetString());
    }
    private static HttpClient Client(AgentCoreApiFactory factory) => factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
    private static async Task Login(HttpClient client, string login, string password)
    { await Csrf(client); Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v2/auth/login", new { login, password })).StatusCode); await Csrf(client); }
    private static async Task Csrf(HttpClient client)
    { var token = (await Json(await client.GetAsync("/api/v2/auth/csrf"))).GetProperty("token").GetString(); client.DefaultRequestHeaders.Remove("X-AgentCore-CSRF"); client.DefaultRequestHeaders.Add("X-AgentCore-CSRF", token); }
    private static async Task<long> Revision(HttpClient client, Guid tenant) => (await Json(await client.GetAsync($"/api/v2/tenants/{tenant}/access/me"))).GetProperty("revision").GetInt64();
    internal static async Task<string> PrivateWorkspacePath(HttpClient owner, Guid tenant, Guid instance)
    {
        var spaceId = await NativePrivateSpaceId(owner, tenant, instance);
        return $"/api/v2/tenants/{tenant}/spaces/{spaceId}/workspace";
    }
    private static async Task<Guid> NativePrivateSpaceId(HttpClient owner, Guid tenant, Guid instance) =>
        (await Json(await owner.GetAsync($"/api/v2/tenants/{tenant}/agents/{instance}/private-space"))).GetProperty("spaceId").GetGuid();
    internal static async Task GrantNativePrivateSpaceRead(HttpClient owner, Guid tenant, Guid instance, Guid? contextPrincipal = null)
    {
        var api = $"/api/v2/tenants/{tenant}";
        var agent = await Json(await owner.GetAsync(api + $"/agents/{instance}"));
        var space = await Json(await owner.GetAsync(api + $"/agents/{instance}/private-space"));
        var principal = contextPrincipal ?? agent.GetProperty("principalId").GetGuid();
        var spaceId = space.GetProperty("spaceId").GetString();
        var access = await Json(await owner.PostAsJsonAsync(api + "/access/check", new { principalId = principal,
            permission = "spaces.read", resourceType = "space", resourceId = spaceId }));
        if (access.GetProperty("allowed").GetBoolean()) return;
        var revision = await Revision(owner, tenant);
        using var created = await owner.PostAsJsonAsync(api + "/roles", new { name = "Fixture context " + instance.ToString("N") + "-" + revision,
            permissions = new[] { "spaces.read" }, expectedRevision = revision });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var role = await Json(created);
        using var assigned = await owner.PostAsJsonAsync(api + "/role-assignments", new { principalId = principal,
            roleId = role.GetProperty("roleId").GetGuid(), resourceType = "space", resourceId = spaceId,
            expectedRevision = await Revision(owner, tenant) });
        Assert.Equal(HttpStatusCode.Created, assigned.StatusCode);
    }
    private static async Task<JsonElement> Json(HttpResponseMessage response)
    { using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return body.RootElement.Clone(); }
}
