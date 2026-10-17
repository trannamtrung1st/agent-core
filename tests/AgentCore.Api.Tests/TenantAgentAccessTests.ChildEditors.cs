using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AgentCore.Api.Tests;

public sealed partial class TenantAgentAccessTests
{
    [Fact]
    public async Task Child_editor_HTTP_reads_filter_scope_and_feed_independent_typed_commands_without_IAM_inspection()
    {
        await using var factory = new AgentCoreApiFactory();
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var root = $"/api/v2/tenants/{tenant}";
        var created = await Json(await owner.PostAsJsonAsync(root + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) }));
        var id = created.GetProperty("instance").GetProperty("instanceId").GetGuid(); var agent = root + $"/agents/{id}";
        long ir = 1;
        async Task<JsonElement> Skill(string key)
        {
            var value = await Json(await owner.PostAsJsonAsync(agent + "/skills/commands", new { operation = "create", expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = ir,
                input = new { id = key, name = key + " review", description = "Regional procedure", procedure = "Compare totals.", projection = "OnDemand", enabled = true, requiredCapabilities = new[] { "chat.respond" } } }));
            ir = value.GetProperty("instanceRevision").GetInt64(); return value.GetProperty("skill");
        }
        async Task<JsonElement> File(string path)
        {
            var value = await Json(await owner.PostAsJsonAsync(agent + "/resources", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = ir,
                input = new { logicalPath = path, kind = 2, mediaType = "text/plain", content = System.Text.Encoding.UTF8.GetBytes("Retained evidence.") } }));
            ir = value.GetProperty("instanceRevision").GetInt64(); return value.GetProperty("resource");
        }
        var visibleSkill = await Skill("visible"); await Skill("private-sentinel");
        var visibleResource = await File("regional/visible.txt"); var hiddenResource = await File("private-sentinel.txt");
        var before = await Revision(owner, tenant);
        foreach (var family in new[] { "skills", "resources" })
        {
            var response = await owner.GetAsync(agent + $"/{family}/editor?pageSize=1&search=VISIBLE"); var view = await Json(response);
            Assert.True(response.Headers.CacheControl!.NoStore); Assert.True(view.GetProperty("allReadable").GetBoolean());
            Assert.True(view.GetProperty("canCreate").GetBoolean()); Assert.Equal(1, view.GetProperty("total").GetInt32()); Assert.Single(view.GetProperty("items").EnumerateArray());
            Assert.Equal(ir, view.GetProperty("agent").GetProperty("instance").GetProperty("revision").GetInt64()); Assert.Equal(before, view.GetProperty("securityRevision").GetInt64());
            Assert.Empty((await Json(await owner.GetAsync(agent + $"/{family}/editor?search=missing"))).GetProperty("items").EnumerateArray());
            foreach (var query in new[] { "page=-1", "page=100001", "pageSize=0", "pageSize=101", "search=" + new string('x', 129) })
                Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync(agent + $"/{family}/editor?{query}")).StatusCode);
        }
        Assert.Equal(before, await Revision(owner, tenant));
        async Task<Guid> Role(string name, params string[] permissions) => (await Json(await owner.PostAsJsonAsync(root + "/roles", new { name, permissions, expectedRevision = await Revision(owner, tenant) }))).GetProperty("roleId").GetGuid();
        var memberRole = await Role("Child editor minimal membership", "sessions.read");
        var parentRole = await Role("Child editor parent reader", "agents.read");
        var childRole = await Role("Child editor exact operations", "skills.read", "skills.manage", "resources.read", "resources.delete");
        var invitation = await Json(await owner.PostAsJsonAsync(root + "/members/invite", new { login = "child-editor@example.test", roleId = memberRole, expectedRevision = await Revision(owner, tenant) }));
        using var member = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await Csrf(member);
        Assert.Equal(HttpStatusCode.Created, (await member.PostAsJsonAsync("/api/v2/auth/register", new { login = "child-editor@example.test", displayName = "Child editor", password = "strong-child-editor-password",
            invitation = new { tenantId = tenant, invitationId = invitation.GetProperty("invitationId").GetGuid(), token = invitation.GetProperty("token").GetString() } })).StatusCode);
        await Login(member, "child-editor@example.test", "strong-child-editor-password");
        var principal = (await Json(await owner.GetAsync(root + "/members"))).GetProperty("items").EnumerateArray().Single(m => m.GetProperty("displayName").GetString() == "Child editor").GetProperty("principalId").GetGuid();
        async Task<JsonElement> Grant(Guid roleId, string resourceType, string resourceId) => await Json(await owner.PostAsJsonAsync(root + "/role-assignments", new { principalId = principal, roleId, resourceType, resourceId, expectedRevision = await Revision(owner, tenant) }));
        var parent = await Grant(parentRole, "agent", id.ToString("D"));
        foreach (var family in new[] { "skills", "resources" }) Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync(agent + $"/{family}/editor")).StatusCode);
        await Grant(childRole, "skill", id.ToString("D") + ":" + visibleSkill.GetProperty("key").GetString());
        await Grant(childRole, "resource", id.ToString("D") + ":" + visibleResource.GetProperty("key").GetString());
        var skills = await Json(await member.GetAsync(agent + "/skills/editor")); var resources = await Json(await member.GetAsync(agent + "/resources/editor"));
        foreach (var view in new[] { skills, resources })
        {
            Assert.Equal(1, view.GetProperty("total").GetInt32()); Assert.False(view.GetProperty("canCreate").GetBoolean()); Assert.False(view.GetProperty("allReadable").GetBoolean());
            Assert.DoesNotContain("private-sentinel", view.GetRawText());
        }
        var detail = await Json(await member.GetAsync(agent + "/skills/" + Uri.EscapeDataString(visibleSkill.GetProperty("key").GetString()!) + "/editor"));
        Assert.Equal("Compare totals.", detail.GetProperty("item").GetProperty("skill").GetProperty("procedure").GetString());
        Assert.True(detail.GetProperty("item").GetProperty("canManage").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync(agent + "/skills/instance%3Aprivate-sentinel/editor")).StatusCode);
        Assert.True(skills.GetProperty("items")[0].GetProperty("canManage").GetBoolean());
        Assert.Equal("Instance", skills.GetProperty("items")[0].GetProperty("skill").GetProperty("origin").GetString());
        Assert.Equal("OnDemand", skills.GetProperty("items")[0].GetProperty("skill").GetProperty("projection").GetString());
        Assert.False(resources.GetProperty("items")[0].GetProperty("canManage").GetBoolean()); Assert.True(resources.GetProperty("items")[0].GetProperty("canDelete").GetBoolean());
        var resourceDetail = await Json(await member.GetAsync(agent + "/resources/" + Uri.EscapeDataString(visibleResource.GetProperty("key").GetString()!) + "/editor"));
        Assert.Equal(resources.GetProperty("securityRevision").GetInt64(), resourceDetail.GetProperty("securityRevision").GetInt64());
        Assert.Equal(resources.GetProperty("items")[0].GetRawText(), resourceDetail.GetProperty("item").GetRawText());
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync(agent + "/resources/" + Uri.EscapeDataString(hiddenResource.GetProperty("key").GetString()!) + "/editor")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync(root + "/roles")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await member.PostAsJsonAsync(agent + "/skills/commands", new { operation = "set_enabled", key = visibleSkill.GetProperty("key").GetString(), enabled = false,
            expectedRevision = skills.GetProperty("securityRevision").GetInt64(), expectedInstanceRevision = ir, expectedSkillRevision = visibleSkill.GetProperty("revision").GetInt64() })).StatusCode);
        var stale = await member.DeleteAsync(agent + "/resources/" + Uri.EscapeDataString(visibleResource.GetProperty("key").GetString()!) + $"?expectedRevision={resources.GetProperty("securityRevision").GetInt64()}&expectedInstanceRevision={ir}&expectedResourceRevision=1");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        resources = await Json(await member.GetAsync(agent + "/resources/editor"));
        Assert.Equal(HttpStatusCode.OK, (await member.DeleteAsync(agent + "/resources/" + Uri.EscapeDataString(visibleResource.GetProperty("key").GetString()!) + $"?expectedRevision={resources.GetProperty("securityRevision").GetInt64()}&expectedInstanceRevision={resources.GetProperty("agent").GetProperty("instance").GetProperty("revision").GetInt64()}&expectedResourceRevision=1")).StatusCode);
        Assert.False((await Json(await member.GetAsync(agent + "/skills/editor"))).GetProperty("items")[0].GetProperty("skill").GetProperty("enabled").GetBoolean());
        await owner.DeleteAsync(root + $"/role-assignments/{parent.GetProperty("assignmentId").GetGuid()}?expectedRevision={await Revision(owner, tenant)}");
        var foreign = (await Json(await owner.PostAsJsonAsync("/api/v2/system/tenants", new { name = "Foreign child scope", slug = "foreign-child-scope" }))).GetProperty("tenantId").GetGuid();
        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        foreach (var family in new[] { "skills", "resources" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync(agent + $"/{family}/editor")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/v2/tenants/{foreign}/agents/{id}/{family}/editor")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(agent + $"/{family}/editor")).StatusCode);
        }
    }
}
