using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AgentCore.Api.Tests;

public sealed partial class TenantAgentAccessTests
{
    [Fact]
    public async Task Creation_scoped_inventory_and_archive_restore_use_current_tenant_authority()
    {
        await using var factory = new AgentCoreApiFactory();
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var a = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var templates = await owner.GetAsync($"/api/v2/tenants/{a}/agent-templates");
        Assert.Equal(HttpStatusCode.OK, templates.StatusCode);
        Assert.Contains((await Json(templates)).GetProperty("items").EnumerateArray(), item => item.GetProperty("id").GetString() == "examiner");
        var first = await owner.PostAsJsonAsync($"/api/v2/tenants/{a}/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, a) });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var agent = await Json(first); var id = agent.GetProperty("instance").GetProperty("instanceId").GetGuid();
        var agentPrincipal = agent.GetProperty("principalId").GetGuid();
        var initial = await owner.PostAsJsonAsync($"/api/v2/tenants/{a}/access/check", new { principalId = agentPrincipal, permission = "agents.read", resourceType = "agent", resourceId = id });
        Assert.False((await Json(initial)).GetProperty("allowed").GetBoolean());
        var b = (await Json(await owner.PostAsJsonAsync("/api/v2/system/tenants", new { name = "Other organization", slug = "other-organization" }))).GetProperty("tenantId").GetGuid();
        Assert.Equal(0, (await Json(await owner.GetAsync($"/api/v2/tenants/{b}/agents"))).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/v2/tenants/{b}/agents/{id}")).StatusCode);
        var second = await owner.PostAsJsonAsync($"/api/v2/tenants/{a}/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, a) });
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var secondId = (await Json(second)).GetProperty("instance").GetProperty("instanceId").GetGuid();
        var paged = await Json(await owner.GetAsync($"/api/v2/tenants/{a}/agents?pageSize=1"));
        Assert.Equal(2, paged.GetProperty("total").GetInt32()); Assert.Single(paged.GetProperty("items").EnumerateArray());
        var role = await Json(await owner.PostAsJsonAsync($"/api/v2/tenants/{a}/roles", new { name = "Chat only", permissions = new[] { "agents.chat" }, expectedRevision = await Revision(owner, a) }));
        var invitation = await Json(await owner.PostAsJsonAsync($"/api/v2/tenants/{a}/members/invite", new { login = "agent-reader@example.test", roleId = role.GetProperty("roleId").GetGuid(), expectedRevision = await Revision(owner, a) }));
        using var member = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await Csrf(member);
        var registered = await member.PostAsJsonAsync("/api/v2/auth/register", new { login = "agent-reader@example.test", displayName = "Agent reader", password = "strong-agent-reader-password",
            invitation = new { tenantId = a, invitationId = invitation.GetProperty("invitationId").GetGuid(), token = invitation.GetProperty("token").GetString() } });
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        await Login(member, "agent-reader@example.test", "strong-agent-reader-password");
        Assert.Equal(0, (await Json(await member.GetAsync($"/api/v2/tenants/{a}/agents"))).GetProperty("total").GetInt32());
        var members = await Json(await owner.GetAsync($"/api/v2/tenants/{a}/members"));
        var human = members.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("login").GetString() == "AGENT-READER@EXAMPLE.TEST").GetProperty("principalId").GetGuid();
        var roles = await Json(await owner.GetAsync($"/api/v2/tenants/{a}/roles"));
        var readerRole = roles.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("key").GetString() == "reader").GetProperty("roleId").GetGuid();
        var assigned = await owner.PostAsJsonAsync($"/api/v2/tenants/{a}/role-assignments", new { principalId = human, roleId = readerRole, resourceType = "agent", resourceId = id, expectedRevision = await Revision(owner, a) });
        Assert.Equal(HttpStatusCode.Created, assigned.StatusCode);
        var assignmentId = (await Json(assigned)).GetProperty("assignmentId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync($"/api/v2/tenants/{a}/agents/{id}/effective-config")).StatusCode);
        foreach (var suffix in new[] { "state-isolation", "editor", "configuration-controls", "versions", "active-version/preview?version=1", "settings", "execution-budgets", "self-management" })
        {
            Assert.Equal(HttpStatusCode.OK, (await member.GetAsync($"/api/v2/tenants/{a}/agents/{id}/{suffix}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync($"/api/v2/tenants/{a}/agents/{secondId}/{suffix}")).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PatchAsJsonAsync($"/api/v2/tenants/{a}/agents/{id}/settings/instructions", new { set = new { agentInstructions = "Denied Reader edit" }, clear = Array.Empty<string>(), expectedRevision = await Revision(owner, a), expectedInstanceRevision = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PutAsJsonAsync($"/api/v2/tenants/{a}/agents/{id}/execution-budgets", new { budgets = (object?)null, expectedRevision = await Revision(owner, a), expectedInstanceRevision = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PutAsJsonAsync($"/api/v2/tenants/{a}/agents/{id}/unattended-model", new { catalogKey = (string?)null, expectedRevision = await Revision(owner, a), expectedInstanceRevision = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PutAsJsonAsync($"/api/v2/tenants/{a}/agents/{id}/self-management", new { overrides = new { }, expectedRevision = await Revision(owner, a), expectedInstanceRevision = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync($"/api/v2/tenants/{a}/agents/{secondId}/effective-config")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PutAsJsonAsync($"/api/v2/tenants/{a}/agents/{id}/persona", new { persona = new { name = "Denied", role = "Guide", description = "Denied", tone = "Clear" }, expectedRevision = await Revision(owner, a), expectedInstanceRevision = 1, expectedPersonaRevision = 1 })).StatusCode);
        var isolation = await Json(await member.GetAsync($"/api/v2/tenants/{a}/agents/{id}/state-isolation"));
        Assert.False(isolation.GetProperty("canUpdate").GetBoolean());
        var isolationPlan = await Json(await member.PostAsync($"/api/v2/tenants/{a}/agents/{id}/state-isolation/presets/Stateless", null));
        Assert.Equal(HttpStatusCode.Forbidden, (await ApplyIsolation(member, $"/api/v2/tenants/{a}/agents/{id}/state-isolation", isolationPlan)).StatusCode);
        var editor = await Json(await member.GetAsync($"/api/v2/tenants/{a}/agents/{id}/editor"));
        Assert.False(editor.GetProperty("canUpdate").GetBoolean());
        Assert.Equal(id, editor.GetProperty("agent").GetProperty("instance").GetProperty("instanceId").GetGuid());
        Assert.DoesNotContain(editor.GetProperty("settings").EnumerateArray(), section => section.GetProperty("section").GetString() == "sandboxPolicy");
        Assert.Contains(editor.GetProperty("unavailableSettings").EnumerateArray(), section => section.GetString() == "sandboxPolicy");
        var scoped = await Json(await member.GetAsync($"/api/v2/tenants/{a}/agents?pageSize=1"));
        Assert.Equal(1, scoped.GetProperty("total").GetInt32());
        Assert.Equal(id, scoped.GetProperty("items")[0].GetProperty("instance").GetProperty("instanceId").GetGuid());
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync($"/api/v2/tenants/{a}/agents/{secondId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync($"/api/v2/tenants/{a}/agents", new { templateId = "examiner", version = 1, expectedRevision = 1 })).StatusCode);
        var archived = await owner.PutAsJsonAsync($"/api/v2/tenants/{a}/agents/{id}/lifecycle", new { lifecycle = 1, expectedRevision = await Revision(owner, a), expectedInstanceRevision = 1 });
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        var archive = await Json(archived); Assert.Equal("Archived", archive.GetProperty("instance").GetProperty("lifecycle").GetString());
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/v2/tenants/{a}/agents/{id}/effective-config")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync($"/api/v2/tenants/{a}/agents/{id}/persona", new { persona = new { name = "Archived", role = "Guide", description = "Read only", tone = "Clear" }, expectedRevision = await Revision(owner, a), expectedInstanceRevision = 2, expectedPersonaRevision = 1 })).StatusCode);
        var stale = await owner.PutAsJsonAsync($"/api/v2/tenants/{a}/agents/{id}/lifecycle", new { lifecycle = 0, expectedRevision = await Revision(owner, a), expectedInstanceRevision = 1 });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var restored = await owner.PutAsJsonAsync($"/api/v2/tenants/{a}/agents/{id}/lifecycle", new { lifecycle = 0, expectedRevision = await Revision(owner, a), expectedInstanceRevision = archive.GetProperty("instance").GetProperty("revision").GetInt64() });
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        Assert.Equal(agentPrincipal, (await Json(restored)).GetProperty("principalId").GetGuid());
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/v2/tenants/{a}/role-assignments/{assignmentId}?expectedRevision={await Revision(owner, a)}")).StatusCode);
        Assert.Equal(0, (await Json(await member.GetAsync($"/api/v2/tenants/{a}/agents"))).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync($"/api/v2/tenants/{a}/agents/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync($"/api/v2/tenants/{a}/agents/{id}/effective-config")).StatusCode);
        foreach (var suffix in new[] { "state-isolation", "editor", "configuration-controls", "versions", "active-version/preview?version=1", "settings", "execution-budgets", "self-management" })
            Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync($"/api/v2/tenants/{a}/agents/{id}/{suffix}")).StatusCode);
    }

    private static async Task Login(HttpClient client, string login, string password)
    { await Csrf(client); Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v2/auth/login", new { login, password })).StatusCode); await Csrf(client); }
    private static async Task Csrf(HttpClient client)
    { var token = (await Json(await client.GetAsync("/api/v2/auth/csrf"))).GetProperty("token").GetString(); client.DefaultRequestHeaders.Remove("X-AgentCore-CSRF"); client.DefaultRequestHeaders.Add("X-AgentCore-CSRF", token); }
    private static async Task<long> Revision(HttpClient client, Guid tenant) => (await Json(await client.GetAsync($"/api/v2/tenants/{tenant}/access/me"))).GetProperty("revision").GetInt64();
    private static async Task<JsonElement> Json(HttpResponseMessage response)
    { using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return json.RootElement.Clone(); }
}
