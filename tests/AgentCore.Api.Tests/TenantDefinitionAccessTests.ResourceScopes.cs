using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AgentCore.Api.Tests;

public sealed partial class TenantDefinitionAccessTests
{
    [Fact]
    public async Task Definition_resource_scopes_filter_content_and_retire_on_publish_fork_and_delete()
    {
        await using var factory = new AgentCoreApiFactory();
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var prefix = $"/api/v2/tenants/{tenant}";
        var draft = await Json(await owner.PostAsJsonAsync($"{prefix}/agent-definitions", new { name = "Scoped Definition resources", expectedRevision = await Revision(owner, tenant) }));
        var definition = draft.GetProperty("definitionId").GetString()!; var id = draft.GetProperty("draftId").GetGuid(); var path = $"{prefix}/definition-drafts/{id}";
        async Task<long> Dr() => (await Json(await owner.GetAsync(path))).GetProperty("revision").GetInt64();
        var upload = await Json(await owner.PostAsJsonAsync($"{path}/resources/batch", new { expectedRevision = await Revision(owner, tenant), expectedDraftRevision = await Dr(), items = new[] {
            new { logicalPath = "visible.txt", kind = 2, mediaType = "text/plain", content = System.Text.Encoding.UTF8.GetBytes("Visible file bytes.") },
            new { logicalPath = "hidden-resource-sentinel.txt", kind = 2, mediaType = "text/plain", content = System.Text.Encoding.UTF8.GetBytes("Hidden file bytes.") } } }));
        var file = upload.GetProperty("resources").EnumerateArray().Single(r => r.GetProperty("logicalPath").GetString() == "visible.txt").GetProperty("resourceId").GetGuid();
        var hidden = upload.GetProperty("resources").EnumerateArray().Single(r => r.GetProperty("resourceId").GetGuid() != file).GetProperty("resourceId").GetGuid();
        var chat = await Json(await owner.PostAsJsonAsync($"{prefix}/roles", new { name = "Resource chat only", permissions = new[] { "agents.chat" }, expectedRevision = await Revision(owner, tenant) }));
        var invitation = await Json(await owner.PostAsJsonAsync($"{prefix}/members/invite", new { login = "definition-file-editor@example.test", roleId = chat.GetProperty("roleId").GetGuid(), expectedRevision = await Revision(owner, tenant) }));
        using var editor = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await Csrf(editor);
        Assert.Equal(HttpStatusCode.Created, (await editor.PostAsJsonAsync("/api/v2/auth/register", new { login = "definition-file-editor@example.test", displayName = "Definition file editor", password = "strong-file-editor-password",
            invitation = new { tenantId = tenant, invitationId = invitation.GetProperty("invitationId").GetGuid(), token = invitation.GetProperty("token").GetString() } })).StatusCode);
        await Login(editor, "definition-file-editor@example.test", "strong-file-editor-password");
        var principal = (await Json(await owner.GetAsync($"{prefix}/members"))).GetProperty("items").EnumerateArray().Single(m => m.GetProperty("login").GetString() == "DEFINITION-FILE-EDITOR@EXAMPLE.TEST").GetProperty("principalId").GetGuid();
        var roles = (await Json(await owner.GetAsync($"{prefix}/roles"))).GetProperty("items"); var reader = roles.EnumerateArray().Single(r => r.GetProperty("key").GetString() == "reader").GetProperty("roleId").GetGuid();
        var rootEditor = await Json(await owner.PostAsJsonAsync($"{prefix}/roles", new { name = "Definition content editor", permissions = new[] { "agent-definitions.read", "agent-definitions.update", "agent-definitions.publish" }, expectedRevision = await Revision(owner, tenant) }));
        var rootGrant = await Json(await owner.PostAsJsonAsync($"{prefix}/role-assignments", new { principalId = principal, roleId = rootEditor.GetProperty("roleId").GetGuid(), resourceType = "definition", resourceId = definition, expectedRevision = await Revision(owner, tenant) }));
        var state = await Json(await editor.GetAsync($"{path}/state")); Assert.Empty(state.GetProperty("resources").EnumerateArray());
        Assert.False(state.GetProperty("resourceAccess").GetProperty("canCreate").GetBoolean()); Assert.False(state.GetProperty("resourceAccess").GetProperty("canPublish").GetBoolean());
        Assert.DoesNotContain("hidden-resource-sentinel", state.GetRawText());
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.GetAsync($"{path}/resources/{file}/content")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.PostAsJsonAsync($"{path}/resources/batch", new { expectedRevision = await Revision(owner, tenant), expectedDraftRevision = await Dr(), items = new[] { new { resourceId = file, logicalPath = "forbidden.txt", kind = 2, mediaType = "text/plain" } } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.PostAsJsonAsync($"{path}/resources/batch", new { expectedRevision = 1, expectedDraftRevision = await Dr(), items = new[] { new { resourceId = file, logicalPath = "forbidden-stale.txt", kind = 2, mediaType = "text/plain" } } })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync($"{path}/resources/batch", new { expectedRevision = 1, expectedDraftRevision = await Dr(), items = new[] { new { resourceId = file, logicalPath = "stale-owner.txt", kind = 2, mediaType = "text/plain" } } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.PostAsJsonAsync($"{path}/publish", new { expectedRevision = await Revision(owner, tenant), expectedDraftRevision = await Dr() })).StatusCode);
        await Json(await editor.PutAsJsonAsync($"{path}/evaluation-scenarios/resource-access", new { title = "Resource access", prompt = "Check the bound resource", requirementLevel = 1, checkType = 2, toolName = "visible.txt", expectedRevision = await Revision(owner, tenant), expectedDraftRevision = await Dr() }));
        var fileRole = await Json(await owner.PostAsJsonAsync($"{prefix}/roles", new { name = "Exact Definition file editor", permissions = new[] { "resources.read", "resources.manage", "resources.delete", "iam.roles.read" }, expectedRevision = await Revision(owner, tenant) }));
        var scope = $"{definition}:draft:{id:D}:{file:D}";
        var grant = await Json(await owner.PostAsJsonAsync($"{prefix}/role-assignments", new { principalId = principal, roleId = fileRole.GetProperty("roleId").GetGuid(), resourceType = "resource", resourceId = scope, expectedRevision = await Revision(owner, tenant) }));
        var malformed = $"{definition}:draft:{Guid.NewGuid():D}:{file:D}";
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync($"{prefix}/role-assignments", new { principalId = principal, roleId = reader, resourceType = "resource", resourceId = malformed, expectedRevision = await Revision(owner, tenant) })).StatusCode);
        state = await Json(await editor.GetAsync($"{path}/state")); Assert.Single(state.GetProperty("resources").EnumerateArray());
        Assert.True(state.GetProperty("resourceAccess").GetProperty("items")[0].GetProperty("canManage").GetBoolean());
        Assert.False(state.GetProperty("resourceAccess").GetProperty("canCreate").GetBoolean()); Assert.False(state.GetProperty("resourceAccess").GetProperty("canEvaluate").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.PostAsJsonAsync($"{path}/evaluation-scenarios/resource-access/run", new { expectedRevision = await Revision(owner, tenant), expectedDraftRevision = await Dr() })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.PostAsJsonAsync($"{path}/resources/batch", new { expectedRevision = await Revision(owner, tenant), expectedDraftRevision = await Dr(), items = new[] { new { logicalPath = "unauthorized.txt", kind = 2, mediaType = "text/plain", content = new byte[] { 65 } } } })).StatusCode);
        Assert.Equal("Visible file bytes.", await editor.GetStringAsync($"{path}/resources/{file}/content"));
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.GetAsync($"{path}/resources/{hidden}/content")).StatusCode);
        await owner.DeleteAsync($"{prefix}/role-assignments/{rootGrant.GetProperty("assignmentId").GetGuid()}?expectedRevision={await Revision(owner, tenant)}");
        await Json(await owner.PostAsJsonAsync($"{prefix}/role-assignments", new { principalId = principal, roleId = reader, resourceType = "definition", resourceId = definition, expectedRevision = await Revision(owner, tenant) }));
        var renamed = await Json(await editor.PostAsJsonAsync($"{path}/resources/batch", new { expectedRevision = await Revision(owner, tenant), expectedDraftRevision = await Dr(), items = new[] { new { resourceId = file, logicalPath = "renamed-visible.txt", kind = 2, mediaType = "text/plain" } } }));
        Assert.Single(renamed.GetProperty("resources").EnumerateArray()); Assert.DoesNotContain("hidden-resource-sentinel", renamed.GetRawText());
        Assert.Equal(HttpStatusCode.OK, (await editor.GetAsync($"{prefix}/access/resources/resource/{Uri.EscapeDataString(scope)}")).StatusCode);
        var publication = await Json(await owner.PostAsJsonAsync($"{path}/publish", new { expectedRevision = await Revision(owner, tenant), expectedDraftRevision = await Dr() }));
        Assert.Equal(1, publication.GetProperty("version").GetInt32());
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.GetAsync($"{prefix}/access/resources/resource/{Uri.EscapeDataString(scope)}")).StatusCode);
        var published = $"{prefix}/agent-definitions/{definition}/publications/1/resources";
        Assert.Empty((await Json(await editor.GetAsync(published))).GetProperty("items").EnumerateArray());
        var publishedScope = $"{definition}:publication:1:{file:D}";
        await Json(await owner.PostAsJsonAsync($"{prefix}/role-assignments", new { principalId = principal, roleId = fileRole.GetProperty("roleId").GetGuid(), resourceType = "resource", resourceId = publishedScope, expectedRevision = await Revision(owner, tenant) }));
        Assert.Equal("Visible file bytes.", await editor.GetStringAsync($"{published}/{file}/content"));
        var fork = await Json(await owner.PostAsJsonAsync($"{prefix}/agent-definitions/{definition}/drafts", new { version = 1, expectedRevision = await Revision(owner, tenant) })); var forkId = fork.GetProperty("draftId").GetGuid();
        Assert.Empty((await Json(await editor.GetAsync($"{prefix}/definition-drafts/{forkId}/state"))).GetProperty("resources").EnumerateArray());
        var forkScope = $"{definition}:draft:{forkId:D}:{file:D}";
        await Json(await owner.PostAsJsonAsync($"{prefix}/role-assignments", new { principalId = principal, roleId = fileRole.GetProperty("roleId").GetGuid(), resourceType = "resource", resourceId = forkScope, expectedRevision = await Revision(owner, tenant) }));
        Assert.Equal(HttpStatusCode.OK, (await editor.DeleteAsync($"{prefix}/definition-drafts/{forkId}/resources/{file}?expectedRevision={await Revision(owner, tenant)}&expectedDraftRevision=1")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.GetAsync($"{prefix}/access/resources/resource/{Uri.EscapeDataString(forkScope)}")).StatusCode);
        Assert.Equal("Visible file bytes.", await editor.GetStringAsync($"{published}/{file}/content"));
        var members = (await Json(await owner.GetAsync($"{prefix}/members"))).GetProperty("items").EnumerateArray().Single(m => m.GetProperty("principalId").GetGuid() == principal);
        Assert.DoesNotContain(members.GetProperty("roles").EnumerateArray(), a => a.GetProperty("assignmentId").GetGuid() == grant.GetProperty("assignmentId").GetGuid());
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"{prefix}/definition-drafts/{forkId}?expectedRevision={await Revision(owner, tenant)}&expectedDraftRevision=2")).StatusCode);
    }
}
