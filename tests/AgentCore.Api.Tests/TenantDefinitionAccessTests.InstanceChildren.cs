using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AgentCore.Api.Tests;

public sealed partial class TenantDefinitionAccessTests
{
    [Fact]
    public async Task Owned_instance_skills_and_resources_commit_actual_children_and_protected_bytes()
    {
        await using var factory = new AgentCoreApiFactory();
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var prefix = $"/api/v2/tenants/{tenant}";
        var draft = await Json(await owner.PostAsJsonAsync($"{prefix}/agent-definitions", new { name = "Owned children", expectedRevision = await Revision(owner, tenant) }));
        var draftId = draft.GetProperty("draftId").GetGuid(); var definition = draft.GetProperty("definitionId").GetString(); var candidate = JsonNode.Parse(draft.GetProperty("candidate").GetRawText())!;
        candidate["skills"] = JsonSerializer.SerializeToNode(new[] { new { id = "review", name = "Review", description = "Review current evidence", procedure = "Compare the supplied totals.", projection = "OnDemand", defaultEnabled = true, requiredCapabilities = new[] { "chat.respond" }, resourcePaths = Array.Empty<string>() } });
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"{prefix}/definition-drafts/{draftId}", new { candidate, expectedRevision = await Revision(owner, tenant), expectedDraftRevision = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"{prefix}/definition-drafts/{draftId}/publish", new { expectedRevision = await Revision(owner, tenant), expectedDraftRevision = 2 })).StatusCode);
        var created = await Json(await owner.PostAsJsonAsync($"{prefix}/agents/from-publication", new { definitionId = definition, version = 1, expectedRevision = await Revision(owner, tenant) }));
        var id = created.GetProperty("instance").GetProperty("instanceId").GetGuid(); var path = $"{prefix}/agents/{id}";
        async Task<long> Ir() => (await Json(await owner.GetAsync(path))).GetProperty("instance").GetProperty("revision").GetInt64();
        async Task<HttpResponseMessage> Skill(string operation, string? key = null, long? revision = null, object? input = null, bool? enabled = null) => await owner.PostAsJsonAsync($"{path}/skills/commands", new { operation, key, expectedSkillRevision = revision, input, enabled, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = await Ir() });
        var listed = await Json(await owner.GetAsync($"{path}/skills?pageSize=1")); Assert.True(listed.GetProperty("total").GetInt32() >= 1); Assert.Single(listed.GetProperty("items").EnumerateArray());
        var customized = await Skill("customize", "definition:review", 1); Assert.Equal(HttpStatusCode.OK, customized.StatusCode);
        var saved = await Json(customized); var localKey = saved.GetProperty("skill").GetProperty("key").GetString();
        Assert.False(saved.GetProperty("definitionSkill").GetProperty("enabled").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await Skill("update", localKey, 1, new { name = "Local review", description = "Independent procedure", procedure = "Private local procedure sentinel.", projection = "OnDemand", enabled = true, requiredCapabilities = new[] { "chat.respond" } })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Skill("delete", localKey, 1)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Skill("reset_enabled", "definition:review", 2)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Skill("delete", "builtin:configuration-assistance", 1)).StatusCode);
        var bytes = System.Text.Encoding.UTF8.GetBytes("Owned file contents.");
        async Task<HttpResponseMessage> Resource(object input) => await owner.PostAsJsonAsync($"{path}/resources", new { input, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = await Ir() });
        var upload = await Resource(new { logicalPath = "notes/owned.txt", kind = 2, mediaType = "text/plain", content = bytes }); Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var resource = (await Json(upload)).GetProperty("resource"); var resourceKey = resource.GetProperty("key").GetString()!; var resourceId = Guid.Parse(resourceKey[9..]);
        var download = await owner.GetAsync($"{path}/resources/{Uri.EscapeDataString(resourceKey)}/content"); Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(bytes, await download.Content.ReadAsByteArrayAsync()); Assert.True(download.Headers.CacheControl!.NoStore);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal(HttpStatusCode.OK, (await Resource(new { resourceId, expectedResourceRevision = 1, logicalPath = "notes/renamed.txt", kind = 2, mediaType = "text/plain" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Resource(new { logicalPath = "forged.txt", kind = 2, mediaType = "text/plain", contentSha256 = resource.GetProperty("contentSha256").GetString() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Resource(new { logicalPath = "../escape.txt", kind = 2, mediaType = "text/plain", content = bytes })).StatusCode);
        TestOwnerCapability.Apply(owner, factory.Services);
        var legacy = $"/api/v2/admin/agent-instances/{id}";
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"{legacy}/skills/{Uri.EscapeDataString(localKey!)}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"{legacy}/resources/{Uri.EscapeDataString(resourceKey)}/content")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.DeleteAsync($"{legacy}/skills/{Uri.EscapeDataString(localKey!)}?expectedRevision=2")).StatusCode);
        var chatRole = await Json(await owner.PostAsJsonAsync($"{prefix}/roles", new { name = "Child chat only", permissions = new[] { "agents.chat" }, expectedRevision = await Revision(owner, tenant) }));
        var invitation = await Json(await owner.PostAsJsonAsync($"{prefix}/members/invite", new { login = "child-reader@example.test", roleId = chatRole.GetProperty("roleId").GetGuid(), expectedRevision = await Revision(owner, tenant) }));
        using var reader = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await Csrf(reader);
        Assert.Equal(HttpStatusCode.Created, (await reader.PostAsJsonAsync("/api/v2/auth/register", new { login = "child-reader@example.test", displayName = "Child reader", password = "strong-child-reader-password", invitation = new { tenantId = tenant, invitationId = invitation.GetProperty("invitationId").GetGuid(), token = invitation.GetProperty("token").GetString() } })).StatusCode);
        await Login(reader, "child-reader@example.test", "strong-child-reader-password");
        var human = (await Json(await owner.GetAsync($"{prefix}/members"))).GetProperty("items").EnumerateArray().Single(m => m.GetProperty("login").GetString() == "CHILD-READER@EXAMPLE.TEST").GetProperty("principalId").GetGuid();
        var readerRole = (await Json(await owner.GetAsync($"{prefix}/roles"))).GetProperty("items").EnumerateArray().Single(r => r.GetProperty("key").GetString() == "reader").GetProperty("roleId").GetGuid();
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync($"{prefix}/role-assignments", new { principalId = human, roleId = readerRole, resourceType = "agent", resourceId = id, expectedRevision = await Revision(owner, tenant) })).StatusCode);
        var parentOnly = await reader.GetAsync($"{path}/effective-config"); Assert.Equal(HttpStatusCode.OK, parentOnly.StatusCode);
        var redacted = await Json(parentOnly); Assert.False(redacted.GetProperty("childAccess").GetProperty("skills").GetBoolean());
        Assert.False(redacted.GetProperty("childAccess").GetProperty("resources").GetBoolean());
        Assert.Empty(redacted.GetProperty("skills").EnumerateArray()); Assert.Empty(redacted.GetProperty("definition").GetProperty("skillList").EnumerateArray());
        Assert.Empty(redacted.GetProperty("resources").EnumerateArray()); Assert.DoesNotContain("Private local procedure sentinel", redacted.GetRawText());
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync($"{path}/skills")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync($"{path}/resources/{Uri.EscapeDataString(resourceKey)}/content")).StatusCode);
        var childrenRole = await Json(await owner.PostAsJsonAsync($"{prefix}/roles", new { name = "Child content reader", permissions = new[] { "skills.read", "resources.read" }, expectedRevision = await Revision(owner, tenant) }));
        var readGrant = await Json(await owner.PostAsJsonAsync($"{prefix}/role-assignments", new { principalId = human, roleId = childrenRole.GetProperty("roleId").GetGuid(), expectedRevision = await Revision(owner, tenant) }));
        Assert.Contains("Private local procedure sentinel", (await reader.GetStringAsync($"{path}/skills")));
        Assert.Equal(bytes, await reader.GetByteArrayAsync($"{path}/resources/{Uri.EscapeDataString(resourceKey)}/content"));
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync($"{path}/skills/commands", new { operation = "delete", key = localKey, expectedSkillRevision = 2, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = await Ir() })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"{prefix}/role-assignments/{readGrant.GetProperty("assignmentId").GetGuid()}?expectedRevision={await Revision(owner, tenant)}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync($"{path}/resources/{Uri.EscapeDataString(resourceKey)}/content")).StatusCode);
        var other = (await Json(await owner.PostAsJsonAsync("/api/v2/system/tenants", new { name = "Other children", slug = "other-children" }))).GetProperty("tenantId").GetGuid();
        foreach (var suffix in new[] { "skills", "resources", $"resources/{Uri.EscapeDataString(resourceKey)}/content" }) Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/v2/tenants/{other}/agents/{id}/{suffix}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Skill("delete", localKey, 2)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.DeleteAsync($"{path}/resources/{Uri.EscapeDataString(resourceKey)}?expectedRevision={await Revision(owner, tenant)}&expectedInstanceRevision={await Ir()}&expectedResourceRevision=2")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"{path}/resources/{Uri.EscapeDataString(resourceKey)}/content")).StatusCode);
    }

    [Fact]
    public async Task Owned_instance_inherited_resource_copy_has_independent_owned_content_and_enable_choices()
    {
        await using var factory = new AgentCoreApiFactory();
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var prefix = $"/api/v2/tenants/{tenant}";
        var draft = await Json(await owner.PostAsJsonAsync($"{prefix}/agent-definitions/from-template", new { templateId = "customer-support", version = 3, expectedRevision = await Revision(owner, tenant) })); var draftId = draft.GetProperty("draftId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"{prefix}/definition-drafts/{draftId}/publish", new { expectedRevision = await Revision(owner, tenant), expectedDraftRevision = 1 })).StatusCode);
        var created = await Json(await owner.PostAsJsonAsync($"{prefix}/agents/from-publication", new { definitionId = draft.GetProperty("definitionId").GetString(), version = 1, expectedRevision = await Revision(owner, tenant) }));
        var id = created.GetProperty("instance").GetProperty("instanceId").GetGuid(); var path = $"{prefix}/agents/{id}";
        async Task<long> Ir() => (await Json(await owner.GetAsync(path))).GetProperty("instance").GetProperty("revision").GetInt64();
        var catalog = await Json(await owner.GetAsync($"{path}/resources")); var source = catalog.GetProperty("catalog").GetProperty("resources")[0]; var key = source.GetProperty("key").GetString()!;
        var original = await owner.GetByteArrayAsync($"{path}/resources/{Uri.EscapeDataString(key)}/content");
        var copied = await owner.PostAsJsonAsync($"{path}/resources/{Uri.EscapeDataString(key)}/copy", new { logicalPath = "local/support-copy.md", expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = await Ir() });
        Assert.Equal(HttpStatusCode.OK, copied.StatusCode); var copy = (await Json(copied)).GetProperty("resource"); var copyKey = copy.GetProperty("key").GetString()!;
        Assert.NotEqual(key, copyKey); Assert.Equal(original, await owner.GetByteArrayAsync($"{path}/resources/{Uri.EscapeDataString(copyKey)}/content"));
        Assert.Equal(draft.GetProperty("definitionId").GetString(), copy.GetProperty("sourceDefinitionId").GetString());
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"{path}/resources/{Uri.EscapeDataString(key)}/enabled", new { enabled = false, expectedResourceRevision = 0, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = await Ir() })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"{path}/resources/{Uri.EscapeDataString(key)}/enabled", new { enabled = (bool?)null, expectedResourceRevision = 1, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = await Ir() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.DeleteAsync($"{path}/resources/{Uri.EscapeDataString(key)}?expectedRevision={await Revision(owner, tenant)}&expectedInstanceRevision={await Ir()}&expectedResourceRevision=2")).StatusCode);
    }
}
