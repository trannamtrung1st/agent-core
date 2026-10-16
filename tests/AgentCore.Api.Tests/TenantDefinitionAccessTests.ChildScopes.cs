using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AgentCore.Api.Tests;

public sealed partial class TenantDefinitionAccessTests
{
    [Fact]
    public async Task Item_scoped_child_grants_filter_reads_and_mutations_and_retire_with_deleted_children()
    {
        await using var factory = new AgentCoreApiFactory();
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var prefix = $"/api/v2/tenants/{tenant}";
        async Task<Guid> Agent(string templateId = "examiner", int version = 1) => (await Json(await owner.PostAsJsonAsync($"{prefix}/agents", new { templateId, version, expectedRevision = await Revision(owner, tenant) }))).GetProperty("instance").GetProperty("instanceId").GetGuid();
        var id = await Agent(); var other = await Agent(); var path = $"{prefix}/agents/{id}";
        async Task<long> Ir(Guid target) => (await Json(await owner.GetAsync($"{prefix}/agents/{target}"))).GetProperty("instance").GetProperty("revision").GetInt64();
        async Task<HttpResponseMessage> Skill(HttpClient client, Guid target, string operation, string? key = null, long? child = null, string name = "Scoped review") =>
            await client.PostAsJsonAsync($"{prefix}/agents/{target}/skills/commands", new { operation, key, expectedSkillRevision = child, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = await Ir(target),
                input = new { id = "shared-readable-id", name, description = "Review totals", procedure = name + " procedure.", projection = "OnDemand", enabled = true, requiredCapabilities = new[] { "chat.respond" } } });
        var skill = (await Json(await Skill(owner, id, "create"))).GetProperty("skill"); var key = skill.GetProperty("key").GetString()!;
        await Json(await Skill(owner, other, "create", name: "Other Instance private"));
        var hidden = await Json(await owner.PostAsJsonAsync($"{path}/skills/commands", new { operation = "create", expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = await Ir(id),
            input = new { id = "hidden", name = "Hidden private Skill", description = "Private", procedure = "Hidden procedure sentinel.", projection = "OnDemand", enabled = true, requiredCapabilities = new[] { "chat.respond" } } }));
        var hiddenKey = hidden.GetProperty("skill").GetProperty("key").GetString()!;
        async Task<System.Text.Json.JsonElement> Upload(string name) => await Json(await owner.PostAsJsonAsync($"{path}/resources", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = await Ir(id),
            input = new { logicalPath = name, kind = 2, mediaType = "text/plain", content = System.Text.Encoding.UTF8.GetBytes(name + " bytes.") } }));
        var file = (await Upload("scoped.txt")).GetProperty("resource"); var fileKey = file.GetProperty("key").GetString()!;
        var hiddenFile = (await Upload("hidden-file-sentinel.txt")).GetProperty("resource").GetProperty("key").GetString()!;
        var chat = await Json(await owner.PostAsJsonAsync($"{prefix}/roles", new { name = "Scoped chat only", permissions = new[] { "agents.chat" }, expectedRevision = await Revision(owner, tenant) }));
        var invitation = await Json(await owner.PostAsJsonAsync($"{prefix}/members/invite", new { login = "item-editor@example.test", roleId = chat.GetProperty("roleId").GetGuid(), expectedRevision = await Revision(owner, tenant) }));
        using var editor = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await Csrf(editor);
        Assert.Equal(HttpStatusCode.Created, (await editor.PostAsJsonAsync("/api/v2/auth/register", new { login = "item-editor@example.test", displayName = "Item editor", password = "strong-item-editor-password",
            invitation = new { tenantId = tenant, invitationId = invitation.GetProperty("invitationId").GetGuid(), token = invitation.GetProperty("token").GetString() } })).StatusCode);
        await Login(editor, "item-editor@example.test", "strong-item-editor-password");
        var principal = (await Json(await owner.GetAsync($"{prefix}/members"))).GetProperty("items").EnumerateArray().Single(m => m.GetProperty("login").GetString() == "ITEM-EDITOR@EXAMPLE.TEST").GetProperty("principalId").GetGuid();
        var reader = (await Json(await owner.GetAsync($"{prefix}/roles"))).GetProperty("items").EnumerateArray().Single(r => r.GetProperty("key").GetString() == "reader").GetProperty("roleId").GetGuid();
        foreach (var target in new[] { id, other })
            await Json(await owner.PostAsJsonAsync($"{prefix}/role-assignments", new { principalId = principal, roleId = reader, resourceType = "agent", resourceId = target, expectedRevision = await Revision(owner, tenant) }));
        var role = await Json(await owner.PostAsJsonAsync($"{prefix}/roles", new { name = "Exact child editor", permissions = new[] { "skills.read", "skills.manage", "resources.read", "resources.manage", "resources.delete", "iam.roles.read", "iam.roles.assign" }, expectedRevision = await Revision(owner, tenant) }));
        async Task<HttpResponseMessage> Grant(HttpClient actor, string type, string target) => await actor.PostAsJsonAsync($"{prefix}/role-assignments", new { principalId = principal, roleId = role.GetProperty("roleId").GetGuid(), resourceType = type, resourceId = target, expectedRevision = await Revision(owner, tenant) });
        var skillScope = id.ToString("D") + ":" + key; var fileScope = id.ToString("D") + ":" + fileKey;
        var skillGrant = await Json(await Grant(owner, "skill", skillScope));
        var fileGrant = await Json(await Grant(owner, "resource", fileScope));
        Assert.Equal(HttpStatusCode.Forbidden, (await Grant(owner, "skill", key)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Grant(owner, "skill", id.ToString("D") + ":instance:missing")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Grant(editor, "skill", other.ToString("D") + ":" + key)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await editor.GetAsync($"{prefix}/access/resources/skill/{Uri.EscapeDataString(skillScope)}")).StatusCode);
        var listed = await Json(await editor.GetAsync($"{path}/skills?pageSize=1"));
        Assert.Equal(1, listed.GetProperty("total").GetInt32()); Assert.Equal(key, listed.GetProperty("items")[0].GetProperty("key").GetString());
        Assert.Empty((await Json(await editor.GetAsync($"{path}/skills?page=1&pageSize=1"))).GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.GetAsync($"{prefix}/agents/{other}/skills")).StatusCode);
        var resources = await Json(await editor.GetAsync($"{path}/resources")); Assert.Single(resources.GetProperty("catalog").GetProperty("resources").EnumerateArray());
        Assert.Equal("scoped.txt bytes.", await editor.GetStringAsync($"{path}/resources/{Uri.EscapeDataString(fileKey)}/content"));
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.GetAsync($"{path}/resources/{Uri.EscapeDataString(hiddenFile)}/content")).StatusCode);
        var config = await Json(await editor.GetAsync($"{path}/effective-config"));
        Assert.False(config.GetProperty("childAccess").GetProperty("allSkills").GetBoolean()); Assert.False(config.GetProperty("childAccess").GetProperty("allResources").GetBoolean());
        Assert.DoesNotContain("Hidden procedure sentinel", config.GetRawText()); Assert.DoesNotContain("hidden-file-sentinel", config.GetRawText());
        Assert.Equal(HttpStatusCode.OK, (await Skill(editor, id, "update", key, 1, "Updated scoped review")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Skill(editor, id, "update", hiddenKey, 1)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Skill(editor, id, "create")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.PostAsJsonAsync($"{path}/resources", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = await Ir(id), input = new { logicalPath = "new.txt", kind = 2, mediaType = "text/plain", content = new byte[] { 65 } } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await editor.PostAsJsonAsync($"{path}/resources", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = await Ir(id), input = new { resourceId = Guid.Parse(fileKey[9..]), expectedResourceRevision = 1, logicalPath = "renamed-scoped.txt", kind = 2, mediaType = "text/plain" } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Skill(editor, id, "delete", key, 2)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.GetAsync($"{prefix}/access/resources/skill/{Uri.EscapeDataString(skillScope)}")).StatusCode);
        var assignments = (await Json(await owner.GetAsync($"{prefix}/members"))).GetProperty("items").EnumerateArray().Single(m => m.GetProperty("principalId").GetGuid() == principal).GetProperty("roles").EnumerateArray().ToArray();
        Assert.DoesNotContain(assignments, a => a.GetProperty("assignmentId").GetGuid() == skillGrant.GetProperty("assignmentId").GetGuid());
        Assert.Contains(assignments, a => a.GetProperty("assignmentId").GetGuid() == fileGrant.GetProperty("assignmentId").GetGuid());
        await Json(await Skill(owner, id, "create", name: "Recreated independent Skill"));
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.GetAsync($"{path}/skills")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await editor.DeleteAsync($"{path}/resources/{Uri.EscapeDataString(fileKey)}?expectedRevision={await Revision(owner, tenant)}&expectedInstanceRevision={await Ir(id)}&expectedResourceRevision=2")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.GetAsync($"{path}/resources")).StatusCode);
        var builtin = await Agent("general-assistant", 32);
        await Json(await owner.PostAsJsonAsync($"{prefix}/role-assignments", new { principalId = principal, roleId = reader, resourceType = "agent", resourceId = builtin, expectedRevision = await Revision(owner, tenant) }));
        await Json(await Grant(owner, "skill", builtin.ToString("D") + ":definition:browser.record.lookup"));
        var builtinSkills = await Json(await editor.GetAsync($"{prefix}/agents/{builtin}/skills"));
        Assert.Equal("definition:browser.record.lookup", Assert.Single(builtinSkills.GetProperty("items").EnumerateArray()).GetProperty("key").GetString());
        var support = await Agent("customer-support", 3);
        await Json(await owner.PostAsJsonAsync($"{prefix}/role-assignments", new { principalId = principal, roleId = reader, resourceType = "agent", resourceId = support, expectedRevision = await Revision(owner, tenant) }));
        var inheritedFile = (await Json(await owner.GetAsync($"{prefix}/agents/{support}/resources"))).GetProperty("catalog").GetProperty("resources")[0].GetProperty("key").GetString()!;
        await Json(await Grant(owner, "resource", support.ToString("D") + ":" + inheritedFile));
        Assert.NotEmpty(await editor.GetByteArrayAsync($"{prefix}/agents/{support}/resources/{Uri.EscapeDataString(inheritedFile)}/content"));
    }
}
