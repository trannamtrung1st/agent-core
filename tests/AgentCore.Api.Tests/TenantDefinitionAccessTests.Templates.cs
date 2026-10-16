using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AgentCore.Api.Tests;

public sealed partial class TenantDefinitionAccessTests
{
    [Fact]
    public async Task Shipped_template_http_fork_uses_definition_create_authority_and_copies_only_approved_owned_content()
    {
        await using var factory = new AgentCoreApiFactory();
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var prefix = $"/api/v2/tenants/{tenant}";
        var role = await Json(await owner.PostAsJsonAsync($"{prefix}/roles", new { name = "Definition creator", permissions = new[] { "agent-definitions.create" }, expectedRevision = await Revision(owner, tenant) }));
        var invitation = await Json(await owner.PostAsJsonAsync($"{prefix}/members/invite", new { login = "template-author@example.test", roleId = role.GetProperty("roleId").GetGuid(), expectedRevision = await Revision(owner, tenant) }));
        using var author = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await Csrf(author);
        Assert.Equal(HttpStatusCode.Created, (await author.PostAsJsonAsync("/api/v2/auth/register", new { login = "template-author@example.test", displayName = "Author", password = "strong-template-password", invitation = new { tenantId = tenant, invitationId = invitation.GetProperty("invitationId").GetGuid(), token = invitation.GetProperty("token").GetString() } })).StatusCode);
        await Login(author, "template-author@example.test", "strong-template-password");
        var authoring = await Json(await author.GetAsync($"{prefix}/definition-authoring-options"));
        Assert.NotEmpty(authoring.GetProperty("authoring").GetProperty("models").EnumerateArray());
        Assert.NotEmpty(authoring.GetProperty("capabilities").EnumerateArray());
        var templates = await Json(await author.GetAsync($"{prefix}/definition-templates"));
        Assert.Contains(templates.GetProperty("items").EnumerateArray(), d => d.GetProperty("id").GetString() == "customer-support" && d.GetProperty("version").GetInt32() == 3);
        Assert.False(templates.GetProperty("canFork").GetProperty("customer-support:3").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await author.GetAsync($"{prefix}/agent-templates")).StatusCode);
        var revision = await Revision(owner, tenant);
        author.DefaultRequestHeaders.Remove("X-AgentCore-CSRF");
        Assert.Equal(HttpStatusCode.BadRequest, (await author.PostAsJsonAsync($"{prefix}/agent-definitions/from-template", new { templateId = "customer-support", version = 3, expectedRevision = revision })).StatusCode);
        await Csrf(author);
        Assert.Equal(HttpStatusCode.Forbidden, (await author.PostAsJsonAsync($"{prefix}/agent-definitions/from-template", new { templateId = "customer-support", version = 999, expectedRevision = revision })).StatusCode);
        Assert.Equal(revision, await Revision(owner, tenant));
        Assert.Equal(HttpStatusCode.Forbidden, (await author.PostAsJsonAsync($"{prefix}/agent-definitions/from-template", new { templateId = "customer-support", version = 3, expectedRevision = revision })).StatusCode);
        Assert.Equal(revision, await Revision(owner, tenant));
        var resourceRole = await Json(await owner.PostAsJsonAsync($"{prefix}/roles", new { name = "Template resource creator", permissions = new[] { "resources.read", "resources.manage" }, expectedRevision = await Revision(owner, tenant) }));
        var principal = (await Json(await owner.GetAsync($"{prefix}/members"))).GetProperty("items").EnumerateArray().Single(m => m.GetProperty("login").GetString() == "TEMPLATE-AUTHOR@EXAMPLE.TEST").GetProperty("principalId").GetGuid();
        await Json(await owner.PostAsJsonAsync($"{prefix}/role-assignments", new { principalId = principal, roleId = resourceRole.GetProperty("roleId").GetGuid(), expectedRevision = await Revision(owner, tenant) }));
        Assert.True((await Json(await author.GetAsync($"{prefix}/definition-templates"))).GetProperty("canFork").GetProperty("customer-support:3").GetBoolean());
        revision = await Revision(owner, tenant);
        var response = await author.PostAsJsonAsync($"{prefix}/agent-definitions/from-template", new { templateId = "customer-support", version = 3, name = "Private support", expectedRevision = revision });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var draft = await Json(response); var id = draft.GetProperty("definitionId").GetString()!; var draftId = draft.GetProperty("draftId").GetGuid();
        Assert.Equal("customer-support", draft.GetProperty("sourceDefinitionId").GetString()); Assert.Equal(3, draft.GetProperty("sourceVersion").GetInt32());
        Assert.NotEqual("customer-support", id); Assert.True(Guid.TryParseExact(id, "D", out _));
        Assert.Equal(HttpStatusCode.Forbidden, (await author.GetAsync($"{prefix}/definition-drafts/{draftId}")).StatusCode);
        var state = await Json(await owner.GetAsync($"{prefix}/definition-drafts/{draftId}/state"));
        var resource = Assert.Single(state.GetProperty("resources").EnumerateArray());
        Assert.Equal("knowledge/support-order-policy", resource.GetProperty("logicalPath").GetString());
        var resourceId = resource.GetProperty("resourceId").GetGuid();
        var bytes = await owner.GetByteArrayAsync($"{prefix}/definition-drafts/{draftId}/resources/{resourceId}/content");
        Assert.NotEmpty(bytes);
        Assert.Equal(HttpStatusCode.Conflict, (await author.PostAsJsonAsync($"{prefix}/agent-definitions/from-template", new { templateId = "customer-support", version = 3, expectedRevision = revision })).StatusCode);
        Assert.Equal(1, (await Json(await owner.GetAsync($"{prefix}/agent-definitions"))).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"{prefix}/definition-drafts/{draftId}/publish", new { expectedRevision = await Revision(owner, tenant), expectedDraftRevision = 1 })).StatusCode);
        Assert.Equal(bytes, await owner.GetByteArrayAsync($"{prefix}/agent-definitions/{id}/publications/1/resources/{resourceId}/content"));
        // An owned durable publication ID is never a shipped-template ID fallback in another tenant.
        var other = (await Json(await owner.PostAsJsonAsync("/api/v2/system/tenants", new { name = "Template second", slug = "template-second" }))).GetProperty("tenantId").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync($"/api/v2/tenants/{other}/agent-definitions/from-template", new { templateId = id, version = 1, expectedRevision = await Revision(owner, other) })).StatusCode);
        Assert.Equal(0, (await Json(await owner.GetAsync($"/api/v2/tenants/{other}/agent-definitions"))).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.Forbidden, (await author.GetAsync($"/api/v2/tenants/{other}/definition-templates")).StatusCode);
        var instance = await owner.PostAsJsonAsync($"{prefix}/agents/from-publication", new { definitionId = Guid.Parse(id), version = 1, expectedRevision = await Revision(owner, tenant) });
        Assert.Equal(HttpStatusCode.Created, instance.StatusCode); Assert.Equal(id, (await Json(instance)).GetProperty("instance").GetProperty("definitionId").GetString());
    }
}
