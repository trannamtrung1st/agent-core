using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Contracts.Http;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AgentCore.Api.Tests;

public sealed class TenantCredentialAccessTests
{
    [Fact]
    public async Task Owned_HTTP_credential_lifecycle_redacts_values_and_blocks_foreign_and_legacy_paths()
    {
        await using var factory = new AgentCoreApiFactory();
        using var owner = Client(factory); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var path = $"/api/v2/tenants/{tenant}/credentials";
        var response = await owner.PostAsJsonAsync(path, new TenantCredentialCreateRequest("Tenant secret", "Password", "General", await Revision(owner, tenant),
            new Dictionary<string, string> { ["username"] = "fixture@example.test" }, ["https://example.test"], "http-private-sentinel"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        var created = await Json(response); var credential = created.GetProperty("credential"); var id = credential.GetProperty("credentialId").GetGuid();
        Safe(created); Assert.Equal(1, credential.GetProperty("revision").GetInt64());
        using var anonymous = Client(factory);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        using var legacy = TestOwnerCapability.CreateOwnerClient(factory);
        Assert.Equal(HttpStatusCode.Unauthorized, (await legacy.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await legacy.GetAsync($"/api/v2/admin/credentials/{id}")).StatusCode);
        Assert.DoesNotContain(id.ToString("D"), await legacy.GetStringAsync("/api/v2/admin/credentials"));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await owner.GetAsync(path + $"/{id}/value")).StatusCode);
        var beta = (await Json(await owner.PostAsJsonAsync("/api/v2/system/tenants", new { name = "Beta", slug = "beta" }))).GetProperty("tenantId").GetGuid();
        Assert.Equal(0, (await Json(await owner.GetAsync($"/api/v2/tenants/{beta}/credentials"))).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/v2/tenants/{beta}/credentials/{id}")).StatusCode);
        var replaced = await owner.PutAsJsonAsync(path + $"/{id}/value", new TenantCredentialReplaceRequest("http-replacement-sentinel", await Revision(owner, tenant), 1));
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode); Safe(await Json(replaced));
        var stale = await owner.PutAsJsonAsync(path + $"/{id}/value", new TenantCredentialReplaceRequest("http-stale-sentinel", await Revision(owner, tenant), 1));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var updated = await owner.PatchAsJsonAsync(path + $"/{id}", new TenantCredentialUpdateRequest("Renamed", "Disabled", await Revision(owner, tenant), 2,
            new Dictionary<string, string> { ["username"] = "fixture@example.test" }, ["https://example.test"]));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode); Safe(await Json(updated));
        var page = await Json(await owner.GetAsync(path + "?pageSize=1")); Assert.Equal(1, page.GetProperty("total").GetInt32()); Safe(page);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync(path + $"/{id}?expectedRevision={await Revision(owner, tenant)}&expectedCredentialRevision=3")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync(path + $"/{id}")).StatusCode);
        Assert.Equal(0, (await Json(await owner.GetAsync(path))).GetProperty("total").GetInt32());
        Safe(await Json(await owner.GetAsync($"/api/v2/tenants/{tenant}/audit")));
    }

    [Fact]
    public async Task Exact_HTTP_credential_grants_allow_only_the_selected_item_and_revoke_before_stale_checks()
    {
        await using var factory = new AgentCoreApiFactory();
        using var owner = Client(factory); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var path = $"/api/v2/tenants/{tenant}";
        async Task<Guid> Create(string name)
        {
            var response = await owner.PostAsJsonAsync(path + "/credentials", new TenantCredentialCreateRequest(name, "Token", "General", await Revision(owner, tenant), ProtectedValue: "http-private-sentinel"));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode); return (await Json(response)).GetProperty("credential").GetProperty("credentialId").GetGuid();
        }
        var first = await Create("First"); var second = await Create("Second");
        var baseRole = await Json(await owner.PostAsJsonAsync(path + "/roles", new { name = "Chat only", permissions = new[] { "agents.chat" }, expectedRevision = await Revision(owner, tenant) }));
        var invitation = await Json(await owner.PostAsJsonAsync(path + "/members/invite", new { login = "credential-writer@example.test", roleId = baseRole.GetProperty("roleId").GetGuid(), expectedRevision = await Revision(owner, tenant) }));
        using var member = Client(factory); await Csrf(member);
        Assert.Equal(HttpStatusCode.Created, (await member.PostAsJsonAsync("/api/v2/auth/register", new { login = "credential-writer@example.test", displayName = "Writer", password = "strong-writer-password",
            invitation = new { tenantId = tenant, invitationId = invitation.GetProperty("invitationId").GetGuid(), token = invitation.GetProperty("token").GetString() } })).StatusCode);
        await Login(member, "credential-writer@example.test", "strong-writer-password");
        var members = await Json(await owner.GetAsync(path + "/members"));
        var user = members.GetProperty("items").EnumerateArray().Single(m => m.GetProperty("login").GetString() == "CREDENTIAL-WRITER@EXAMPLE.TEST");
        var role = await Json(await owner.PostAsJsonAsync(path + "/roles", new { name = "One credential", permissions = new[] { "credentials.read", "credentials.manage" }, expectedRevision = await Revision(owner, tenant) }));
        var assignmentResponse = await owner.PostAsJsonAsync(path + "/role-assignments", new { principalId = user.GetProperty("principalId").GetGuid(), roleId = role.GetProperty("roleId").GetGuid(), resourceType = "credential", resourceId = first, expectedRevision = await Revision(owner, tenant) });
        Assert.Equal(HttpStatusCode.Created, assignmentResponse.StatusCode); var assignment = await Json(assignmentResponse);
        var items = await Json(await member.GetAsync(path + "/credentials?pageSize=1"));
        Assert.Equal(1, items.GetProperty("total").GetInt32());
        Assert.Equal(first, items.GetProperty("items")[0].GetProperty("credential").GetProperty("credentialId").GetGuid());
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync(path + $"/credentials/{second}")).StatusCode);
        var saved = await member.PutAsJsonAsync(path + $"/credentials/{first}/value", new TenantCredentialReplaceRequest("http-replacement-sentinel", await Revision(owner, tenant), 1));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode); Safe(await Json(saved));
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync(path + $"/role-assignments/{assignment.GetProperty("assignmentId").GetGuid()}?expectedRevision={await Revision(owner, tenant)}")).StatusCode);
        var denied = await member.PutAsJsonAsync(path + $"/credentials/{first}/value", new TenantCredentialReplaceRequest("http-stale-sentinel", 1, 1));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(0, (await Json(await member.GetAsync(path + "/credentials"))).GetProperty("total").GetInt32());
    }
    [Fact]
    public async Task Typed_HTTP_profiles_create_disconnected_OAuth_and_validate_password_and_registry_inputs()
    {
        await using var factory = new AgentCoreApiFactory();
        using var owner = Client(factory); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var path = $"/api/v2/tenants/{tenant}/credentials";
        var requests = new TenantCredentialCreateRequest[]
        {
            new("Gmail", "Token", "EmailAccount", 0, EmailAccount: new("Gmail", "")),
            new("Drive", "Token", "GoogleDriveAccount", 0),
            new("Custom", "Password", "EmailAccount", 0, ProtectedValue: "http-private-sentinel", EmailAccount: new("Custom", "fixture@example.test", "fixture@example.test", "fixture@example.test",
                new("mail.example.test", 993, "ImplicitTls"), new("mail.example.test", 465, "ImplicitTls"))),
            new("Registry", "Token", "ContainerRegistry", 0, ProtectedValue: "http-private-sentinel", ContainerRegistry: new("ghcr.io", "fixture", ["ghcr.io/acme"]))
        };
        var saved = new List<JsonElement>();
        foreach (var request in requests)
        {
            var response = await owner.PostAsJsonAsync(path, request with { ExpectedRevision = await Revision(owner, tenant) });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode); var item = await Json(response); Safe(item); saved.Add(item.GetProperty("credential"));
        }
        Assert.Equal("NotConnected", saved[0].GetProperty("emailAccount").GetProperty("connectionStatus").GetString());
        Assert.Equal("NotConnected", saved[1].GetProperty("driveAccount").GetProperty("connectionStatus").GetString());
        var before = await Revision(owner, tenant);
        foreach (var invalid in new[] { requests[0] with { ProtectedValue = "http-private-sentinel" }, requests[2] with { Kind = "ApiKey" }, requests[3] with { AllowedOrigins = ["https://example.test"] } })
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(path, invalid with { ExpectedRevision = before })).StatusCode);
        Assert.Equal(before, await Revision(owner, tenant));
        var gmailId = saved[0].GetProperty("credentialId").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync(path + $"/{gmailId}/value", new TenantCredentialReplaceRequest("http-private-sentinel", before, 1))).StatusCode);
        Safe(await Json(await owner.GetAsync(path)));
    }
    private static HttpClient Client(AgentCoreApiFactory factory) => factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
    private static async Task Login(HttpClient client, string login, string password)
    { await Csrf(client); Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v2/auth/login", new { login, password })).StatusCode); await Csrf(client); }
    private static async Task Csrf(HttpClient client)
    { var token = (await Json(await client.GetAsync("/api/v2/auth/csrf"))).GetProperty("token").GetString(); client.DefaultRequestHeaders.Remove("X-AgentCore-CSRF"); client.DefaultRequestHeaders.Add("X-AgentCore-CSRF", token); }
    private static async Task<long> Revision(HttpClient client, Guid tenant) => (await Json(await client.GetAsync($"/api/v2/tenants/{tenant}/access/me"))).GetProperty("revision").GetInt64();
    private static async Task<JsonElement> Json(HttpResponseMessage response)
    { using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return json.RootElement.Clone(); }
    private static void Safe(JsonElement value)
    {
        var text = value.GetRawText();
        foreach (var forbidden in new[] { "http-private-sentinel", "http-replacement-sentinel", "http-stale-sentinel", "protectedValue", "protectedPayload", "protectionVersion", "secretRevision" })
            Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
    }
}
