using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AgentCore.Api.Tests;

public sealed partial class TenantDefinitionAccessTests
{
    [Fact]
    public async Task Definition_delete_http_preserves_revision_and_reference_recovery_and_requires_current_authority()
    {
        await using var factory = new AgentCoreApiFactory();
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var prefix = $"/api/v2/tenants/{tenant}";
        var draft = await Json(await owner.PostAsJsonAsync($"{prefix}/agent-definitions", new { name = "Delete safely", expectedRevision = await Revision(owner, tenant) }));
        var id = draft.GetProperty("definitionId").GetString()!; var draftId = draft.GetProperty("draftId").GetGuid();
        var stale = await owner.DeleteAsync($"{prefix}/definition-drafts/{draftId}?expectedRevision={await Revision(owner, tenant)}&expectedDraftRevision=2");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode); Assert.Equal("stale_revision", (await Json(stale)).GetProperty("reasonCode").GetString());
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"{prefix}/definition-drafts/{draftId}")).StatusCode);
        var beta = (await Json(await owner.PostAsJsonAsync("/api/v2/system/tenants", new { name = "Other", slug = "delete-other" }))).GetProperty("tenantId").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.DeleteAsync($"/api/v2/tenants/{beta}/definition-drafts/{draftId}?expectedRevision={await Revision(owner, beta)}&expectedDraftRevision=1")).StatusCode);

        // An authenticated Reader can inspect the exact draft but cannot delete it.
        var roles = (await Json(await owner.GetAsync($"{prefix}/roles"))).GetProperty("items");
        var readerRole = roles.EnumerateArray().Single(r => r.GetProperty("key").GetString() == "reader").GetProperty("roleId").GetGuid();
        var invitation = await Json(await owner.PostAsJsonAsync($"{prefix}/members/invite", new { login = "delete-reader@example.test", roleId = readerRole, expectedRevision = await Revision(owner, tenant) }));
        using var reader = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await Csrf(reader);
        Assert.Equal(HttpStatusCode.Created, (await reader.PostAsJsonAsync("/api/v2/auth/register", new { login = "delete-reader@example.test", displayName = "Reader", password = "strong-reader-password", invitation = new { tenantId = tenant, invitationId = invitation.GetProperty("invitationId").GetGuid(), token = invitation.GetProperty("token").GetString() } })).StatusCode);
        await Login(reader, "delete-reader@example.test", "strong-reader-password");
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"{prefix}/definition-drafts/{draftId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteAsync($"{prefix}/definition-drafts/{draftId}?expectedRevision={await Revision(owner, tenant)}&expectedDraftRevision=1")).StatusCode);
        owner.DefaultRequestHeaders.Remove("X-AgentCore-CSRF");
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.DeleteAsync($"{prefix}/definition-drafts/{draftId}?expectedRevision={await Revision(owner, tenant)}&expectedDraftRevision=1")).StatusCode);
        await Csrf(owner);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"{prefix}/definition-drafts/{draftId}?expectedRevision={await Revision(owner, tenant)}&expectedDraftRevision=1")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"{prefix}/definition-drafts/{draftId}")).StatusCode);
        Assert.Equal(0, (await Json(await owner.GetAsync($"{prefix}/agent-definitions"))).GetProperty("total").GetInt32());

        var publishedDraft = await Json(await owner.PostAsJsonAsync($"{prefix}/agent-definitions", new { name = "Published removal", expectedRevision = await Revision(owner, tenant) }));
        id = publishedDraft.GetProperty("definitionId").GetString()!; draftId = publishedDraft.GetProperty("draftId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"{prefix}/definition-drafts/{draftId}/publish", new { expectedRevision = await Revision(owner, tenant), expectedDraftRevision = 1 })).StatusCode);
        var fork = await Json(await owner.PostAsJsonAsync($"{prefix}/agent-definitions/{id}/drafts", new { version = 1, expectedRevision = await Revision(owner, tenant) }));
        var forkId = fork.GetProperty("draftId").GetGuid();
        var witness = new { drafts = new[] { new { draftId = forkId, revision = 1 } }, publications = new[] { new { version = 1, metadataRevision = 1 } } };
        var denied = await DeleteDefinition(reader, $"{prefix}/agent-definitions/{id}", witness, await Revision(owner, tenant));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var incomplete = await DeleteDefinition(owner, $"{prefix}/agent-definitions/{id}", new { drafts = Array.Empty<object>(), publications = witness.publications }, await Revision(owner, tenant));
        Assert.Equal(HttpStatusCode.Conflict, incomplete.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await DeleteDefinition(owner, $"{prefix}/agent-definitions/{id}", witness, await Revision(owner, tenant))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"{prefix}/agent-definitions/{id}/publications/1")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync($"{prefix}/agents/from-publication", new { definitionId = Guid.Parse(id), version = 1, expectedRevision = await Revision(owner, tenant) })).StatusCode);

        var used = await Json(await owner.PostAsJsonAsync($"{prefix}/agent-definitions", new { name = "Keep referenced", expectedRevision = await Revision(owner, tenant) }));
        id = used.GetProperty("definitionId").GetString()!; draftId = used.GetProperty("draftId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"{prefix}/definition-drafts/{draftId}/publish", new { expectedRevision = await Revision(owner, tenant), expectedDraftRevision = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync($"{prefix}/agents/from-publication", new { definitionId = Guid.Parse(id), version = 1, expectedRevision = await Revision(owner, tenant) })).StatusCode);
        var revision = await Revision(owner, tenant);
        var blocked = await DeleteDefinition(owner, $"{prefix}/agent-definitions/{id}", new { drafts = Array.Empty<object>(), publications = witness.publications }, revision);
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode); Assert.Equal("definition_in_use", (await Json(blocked)).GetProperty("reasonCode").GetString());
        Assert.Equal(revision, await Revision(owner, tenant));
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"{prefix}/agent-definitions/{id}/publications/1")).StatusCode);
    }

    private static async Task<HttpResponseMessage> DeleteDefinition(HttpClient client, string url, object witness, long expectedRevision)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, url) { Content = JsonContent.Create(new { witness, expectedRevision }) };
        return await client.SendAsync(request);
    }
}
