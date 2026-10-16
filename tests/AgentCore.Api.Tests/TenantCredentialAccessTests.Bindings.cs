using System.Net;
using System.Net.Http.Json;
using AgentCore.Contracts.Http;

namespace AgentCore.Api.Tests;

public sealed partial class TenantCredentialAccessTests
{
    [Fact]
    public async Task Binding_HTTP_joins_actual_owners_preserves_lifecycle_and_never_exposes_values_or_legacy_authority()
    {
        await using var factory = new AgentCoreApiFactory();
        using var owner = Client(factory); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var root = $"/api/v2/tenants/{tenant}";
        var credential = (await Json(await owner.PostAsJsonAsync(root + "/credentials", new TenantCredentialCreateRequest("Bound secret", "Password", "General", await Revision(owner, tenant),
            AllowedOrigins: ["https://example.test"], ProtectedValue: "http-private-sentinel")))).GetProperty("credential").GetProperty("credentialId").GetGuid();
        var agent = await Json(await owner.PostAsJsonAsync(root + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) }));
        var id = agent.GetProperty("instance").GetProperty("instanceId").GetGuid(); var path = root + $"/agents/{id}/credential-bindings";
        var beta = (await Json(await owner.PostAsJsonAsync("/api/v2/system/tenants", new { name = "Beta", slug = "beta" }))).GetProperty("tenantId").GetGuid();
        var foreign = (await Json(await owner.PostAsJsonAsync($"/api/v2/tenants/{beta}/credentials", new TenantCredentialCreateRequest("Foreign", "Password", "General", await Revision(owner, beta), ProtectedValue: "http-private-sentinel")))).GetProperty("credential").GetProperty("credentialId").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync(path, new TenantBindCredentialRequest(foreign, "foreign", await Revision(owner, tenant), 1, 1))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/v2/tenants/{beta}/agents/{id}/credential-bindings")).StatusCode);
        var registry = (await Json(await owner.PostAsJsonAsync(root + "/credentials", new TenantCredentialCreateRequest("Registry", "Token", "ContainerRegistry", await Revision(owner, tenant),
            ProtectedValue: "http-private-sentinel", ContainerRegistry: new("ghcr.io", "fixture", ["ghcr.io/acme"]))))).GetProperty("credential").GetProperty("credentialId").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync(path, new TenantBindCredentialRequest(registry, "registry", await Revision(owner, tenant), 1, 1))).StatusCode);
        var response = await owner.PostAsJsonAsync(path, new TenantBindCredentialRequest(credential, "PRIMARY", await Revision(owner, tenant), 1, 1));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        var saved = await Json(response); Safe(saved); Assert.Equal(2, saved.GetProperty("instanceRevision").GetInt64());
        var binding = saved.GetProperty("binding").GetProperty("bindingId").GetGuid();
        var page = await Json(await owner.GetAsync(path + "?pageSize=1")); Safe(page);
        Assert.Equal(1, page.GetProperty("total").GetInt32()); Assert.Equal("primary", page.GetProperty("items")[0].GetProperty("reference").GetString());
        Assert.Equal(2, (await Json(await owner.GetAsync(root + $"/agents/{id}"))).GetProperty("instance").GetProperty("revision").GetInt64());
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync(path, new TenantBindCredentialRequest(credential, "duplicate", await Revision(owner, tenant), 2, 1))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.DeleteAsync(root + $"/credentials/{credential}?expectedRevision={await Revision(owner, tenant)}&expectedCredentialRevision=1")).StatusCode);
        using var legacy = TestOwnerCapability.CreateOwnerClient(factory);
        Assert.Equal(HttpStatusCode.Unauthorized, (await legacy.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await legacy.GetAsync($"/api/v2/admin/agent-instances/{id}/credential-bindings")).StatusCode);
        var malformed = await owner.PostAsync(path, new StringContent("{\"credentialId\":\"http-private-sentinel\"}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode); var invalid = await Json(malformed); Safe(invalid);
        Assert.False(invalid.TryGetProperty("diagnosticId", out _));
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(root + $"/agents/{id}/lifecycle", new { lifecycle = 1, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 2 })).StatusCode);
        Assert.Equal(1, (await Json(await owner.GetAsync(path))).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.DeleteAsync(path + $"/{binding}?expectedRevision={await Revision(owner, tenant)}&expectedInstanceRevision=3&expectedBindingRevision=1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(root + $"/agents/{id}/lifecycle", new { lifecycle = 0, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 3 })).StatusCode);
        var removed = await owner.DeleteAsync(path + $"/{binding}?expectedRevision={await Revision(owner, tenant)}&expectedInstanceRevision=4&expectedBindingRevision=1");
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode); var witness = await Json(removed);
        Assert.Equal(5, witness.GetProperty("instanceRevision").GetInt64()); Assert.False(witness.TryGetProperty("binding", out _));
        Assert.Equal(0, (await Json(await owner.GetAsync(path))).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync(root + $"/credentials/{credential}?expectedRevision={await Revision(owner, tenant)}&expectedCredentialRevision=1")).StatusCode);
        var audit = await Json(await owner.GetAsync(root + "/audit")); Safe(audit); Assert.Contains("credentials.bind", audit.GetRawText()); Assert.Contains("credentials.unbind", audit.GetRawText());
    }

    [Fact]
    public async Task Binding_HTTP_requires_agent_update_and_credential_bind_separately_and_revokes_before_stale_checks()
    {
        await using var factory = new AgentCoreApiFactory();
        using var owner = Client(factory); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var root = $"/api/v2/tenants/{tenant}";
        var credential = (await Json(await owner.PostAsJsonAsync(root + "/credentials", new TenantCredentialCreateRequest("Private", "Password", "General", await Revision(owner, tenant), ProtectedValue: "http-private-sentinel")))).GetProperty("credential").GetProperty("credentialId").GetGuid();
        var id = (await Json(await owner.PostAsJsonAsync(root + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) }))).GetProperty("instance").GetProperty("instanceId").GetGuid();
        var parentRole = await Json(await owner.PostAsJsonAsync(root + "/roles", new { name = "Agent read", permissions = new[] { "agents.read" }, expectedRevision = await Revision(owner, tenant) }));
        var invitation = await Json(await owner.PostAsJsonAsync(root + "/members/invite", new { login = "binding-writer@example.test", roleId = parentRole.GetProperty("roleId").GetGuid(), expectedRevision = await Revision(owner, tenant) }));
        using var member = Client(factory); await Csrf(member);
        Assert.Equal(HttpStatusCode.Created, (await member.PostAsJsonAsync("/api/v2/auth/register", new { login = "binding-writer@example.test", displayName = "Writer", password = "strong-writer-password",
            invitation = new { tenantId = tenant, invitationId = invitation.GetProperty("invitationId").GetGuid(), token = invitation.GetProperty("token").GetString() } })).StatusCode);
        await Login(member, "binding-writer@example.test", "strong-writer-password");
        var members = await Json(await owner.GetAsync(root + "/members"));
        var principal = members.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("login").GetString() == "BINDING-WRITER@EXAMPLE.TEST").GetProperty("principalId").GetGuid();
        var credentialRole = await Json(await owner.PostAsJsonAsync(root + "/roles", new { name = "Credential binding", permissions = new[] { "credentials.read", "credentials.bind" }, expectedRevision = await Revision(owner, tenant) }));
        var assignment = await Json(await owner.PostAsJsonAsync(root + "/role-assignments", new { principalId = principal, roleId = credentialRole.GetProperty("roleId").GetGuid(), resourceType = "credential", resourceId = credential, expectedRevision = await Revision(owner, tenant) }));
        var path = root + $"/agents/{id}/credential-bindings";
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync(path, new TenantBindCredentialRequest(credential, "primary", await Revision(owner, tenant), 1, 1))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(root + $"/roles/{parentRole.GetProperty("roleId").GetGuid()}", new { name = "Agent update", permissions = new[] { "agents.read", "agents.update" }, expectedRevision = await Revision(owner, tenant) })).StatusCode);
        var response = await member.PostAsJsonAsync(path, new TenantBindCredentialRequest(credential, "primary", await Revision(owner, tenant), 1, 1));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode); var saved = await Json(response); Safe(saved);
        Assert.False((await Json(await member.GetAsync(path))).GetProperty("allRead").GetBoolean());
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync(root + $"/role-assignments/{assignment.GetProperty("assignmentId").GetGuid()}?expectedRevision={await Revision(owner, tenant)}")).StatusCode);
        Assert.Equal(0, (await Json(await member.GetAsync(path))).GetProperty("total").GetInt32());
        var binding = saved.GetProperty("binding").GetProperty("bindingId").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await member.DeleteAsync(path + $"/{binding}?expectedRevision=1&expectedInstanceRevision=1&expectedBindingRevision=1")).StatusCode);
        Assert.Equal(1, (await Json(await owner.GetAsync(path))).GetProperty("total").GetInt32());
    }
}
