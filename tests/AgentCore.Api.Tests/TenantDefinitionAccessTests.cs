using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantDefinitionAccessTests
{
    [Fact]
    public async Task Author_publish_and_instantiate_use_owned_publications_with_live_scoped_access()
    {
        await using var factory = new AgentCoreApiFactory();
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var draftResponse = await owner.PostAsJsonAsync($"/api/v2/tenants/{tenant}/agent-definitions", new { name = "Organization private assistant", expectedRevision = await Revision(owner, tenant) });
        Assert.Equal(HttpStatusCode.Created, draftResponse.StatusCode);
        var draft = await Json(draftResponse); var id = Guid.Parse(draft.GetProperty("definitionId").GetString()!); var draftId = draft.GetProperty("draftId").GetGuid();
        var admin = factory.Services.GetRequiredService<IAgentDefinitionAdminStore>();
        Assert.Null(await admin.GetDraftAsync(draftId));
        Assert.DoesNotContain(await admin.ListDraftsAsync(), d => d.DefinitionId == id.ToString("D"));
        var other = (await Json(await owner.PostAsJsonAsync("/api/v2/system/tenants", new { name = "Second organization", slug = "definition-second" }))).GetProperty("tenantId").GetGuid();
        Assert.Equal(0, (await Json(await owner.GetAsync($"/api/v2/tenants/{other}/agent-definitions"))).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/v2/tenants/{other}/definition-drafts/{draftId}")).StatusCode);
        var invalidCandidate = JsonNode.Parse(draft.GetProperty("candidate").GetRawText())!;
        invalidCandidate["providerPreferences"]!["languageModel"] = "unconfigured-provider";
        var invalid = await owner.PutAsJsonAsync($"/api/v2/tenants/{tenant}/definition-drafts/{draftId}", new { candidate = invalidCandidate, expectedRevision = await Revision(owner, tenant), expectedDraftRevision = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode); Assert.Equal("unknown_provider_alias", (await Json(invalid)).GetProperty("reasonCode").GetString());
        var candidate = JsonNode.Parse(draft.GetProperty("candidate").GetRawText())!;
        candidate["agentInstructions"] = "Private version one instructions.";
        var updated = await owner.PutAsJsonAsync($"/api/v2/tenants/{tenant}/definition-drafts/{draftId}", new { candidate, expectedRevision = await Revision(owner, tenant), expectedDraftRevision = 1 });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var stale = await owner.PostAsJsonAsync($"/api/v2/tenants/{tenant}/definition-drafts/{draftId}/publish", new { expectedRevision = await Revision(owner, tenant), expectedDraftRevision = 1 });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var published = await owner.PostAsJsonAsync($"/api/v2/tenants/{tenant}/definition-drafts/{draftId}/publish", new { expectedRevision = await Revision(owner, tenant), expectedDraftRevision = 2 });
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        Assert.Equal(1, (await Json(published)).GetProperty("version").GetInt32());
        Assert.Null(await admin.GetPublicationAsync(id.ToString("D"), 1));
        Assert.DoesNotContain(await admin.ListPublicationsAsync(), p => p.DefinitionId == id.ToString("D"));
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/v2/tenants/{tenant}/definition-drafts/{draftId}")).StatusCode);
        var forkResponse = await owner.PostAsJsonAsync($"/api/v2/tenants/{tenant}/agent-definitions/{id}/drafts", new { version = 1, expectedRevision = await Revision(owner, tenant) });
        Assert.Equal(HttpStatusCode.Created, forkResponse.StatusCode);
        var fork = await Json(forkResponse); var forkId = fork.GetProperty("draftId").GetGuid();
        var secondCandidate = JsonNode.Parse(fork.GetProperty("candidate").GetRawText())!; secondCandidate["agentInstructions"] = "Private version two instructions.";
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/v2/tenants/{tenant}/definition-drafts/{forkId}", new { candidate = secondCandidate, expectedRevision = await Revision(owner, tenant), expectedDraftRevision = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"/api/v2/tenants/{tenant}/definition-drafts/{forkId}/publish", new { expectedRevision = await Revision(owner, tenant), expectedDraftRevision = 2 })).StatusCode);
        var first = await Json(await owner.GetAsync($"/api/v2/tenants/{tenant}/agent-definitions/{id}/publications/1"));
        Assert.Equal("Private version one instructions.", first.GetProperty("payload").GetProperty("agentInstructions").GetString());
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"/api/v2/tenants/{tenant}/agent-definitions/{id}/publications/2/deprecate", new { expectedRevision = await Revision(owner, tenant), expectedMetadataRevision = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync($"/api/v2/tenants/{tenant}/agents/from-publication", new { definitionId = id, version = 2, expectedRevision = await Revision(owner, tenant) })).StatusCode);
        var instance = await owner.PostAsJsonAsync($"/api/v2/tenants/{tenant}/agents/from-publication", new { definitionId = id, version = 1, expectedRevision = await Revision(owner, tenant) });
        Assert.Equal(HttpStatusCode.Created, instance.StatusCode);
        Assert.Equal(id.ToString("D"), (await Json(instance)).GetProperty("instance").GetProperty("definitionId").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync($"/api/v2/tenants/{other}/agents/from-publication", new { definitionId = id, version = 1, expectedRevision = await Revision(owner, other) })).StatusCode);
        var secondDefinition = await Json(await owner.PostAsJsonAsync($"/api/v2/tenants/{tenant}/agent-definitions", new { name = "Another private definition", expectedRevision = await Revision(owner, tenant) }));
        var secondDraftId = secondDefinition.GetProperty("draftId").GetGuid();
        var paged = await Json(await owner.GetAsync($"/api/v2/tenants/{tenant}/agent-definitions?pageSize=1"));
        Assert.Equal(2, paged.GetProperty("total").GetInt32()); Assert.Single(paged.GetProperty("items").EnumerateArray());
        var chatRole = await Json(await owner.PostAsJsonAsync($"/api/v2/tenants/{tenant}/roles", new { name = "Chat only", permissions = new[] { "agents.chat" }, expectedRevision = await Revision(owner, tenant) }));
        var invite = await Json(await owner.PostAsJsonAsync($"/api/v2/tenants/{tenant}/members/invite", new { login = "definition-reader@example.test", roleId = chatRole.GetProperty("roleId").GetGuid(), expectedRevision = await Revision(owner, tenant) }));
        using var reader = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await Csrf(reader);
        Assert.Equal(HttpStatusCode.Created, (await reader.PostAsJsonAsync("/api/v2/auth/register", new { login = "definition-reader@example.test", displayName = "Definition reader", password = "strong-definition-password", invitation = new { tenantId = tenant, invitationId = invite.GetProperty("invitationId").GetGuid(), token = invite.GetProperty("token").GetString() } })).StatusCode);
        await Login(reader, "definition-reader@example.test", "strong-definition-password");
        Assert.Equal(0, (await Json(await reader.GetAsync($"/api/v2/tenants/{tenant}/agent-definitions"))).GetProperty("total").GetInt32());
        var people = await Json(await owner.GetAsync($"/api/v2/tenants/{tenant}/members"));
        var principal = people.GetProperty("items").EnumerateArray().Single(p => p.GetProperty("login").GetString() == "DEFINITION-READER@EXAMPLE.TEST").GetProperty("principalId").GetGuid();
        var roles = await Json(await owner.GetAsync($"/api/v2/tenants/{tenant}/roles"));
        var role = roles.GetProperty("items").EnumerateArray().Single(r => r.GetProperty("key").GetString() == "reader").GetProperty("roleId").GetGuid();
        var grantResponse = await owner.PostAsJsonAsync($"/api/v2/tenants/{tenant}/role-assignments", new { principalId = principal, roleId = role, resourceType = "definition", resourceId = id, expectedRevision = await Revision(owner, tenant) });
        Assert.Equal(HttpStatusCode.Created, grantResponse.StatusCode);
        var grant = await Json(grantResponse);
        Assert.Equal(1, (await Json(await reader.GetAsync($"/api/v2/tenants/{tenant}/agent-definitions?pageSize=1"))).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/api/v2/tenants/{tenant}/agent-definitions/{id}/publications/1")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync($"/api/v2/tenants/{tenant}/definition-drafts/{secondDraftId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync($"/api/v2/tenants/{tenant}/agent-definitions", new { name = "Denied", expectedRevision = await Revision(owner, tenant) })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/v2/tenants/{tenant}/role-assignments/{grant.GetProperty("assignmentId").GetGuid()}?expectedRevision={await Revision(owner, tenant)}")).StatusCode);
        Assert.Equal(0, (await Json(await reader.GetAsync($"/api/v2/tenants/{tenant}/agent-definitions"))).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync($"/api/v2/tenants/{tenant}/agent-definitions/{id}/publications/1")).StatusCode);
    }
    private static async Task Login(HttpClient client, string login, string password)
    { await Csrf(client); Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v2/auth/login", new { login, password })).StatusCode); await Csrf(client); }
    private static async Task Csrf(HttpClient client)
    { var token = (await Json(await client.GetAsync("/api/v2/auth/csrf"))).GetProperty("token").GetString(); client.DefaultRequestHeaders.Remove("X-AgentCore-CSRF"); client.DefaultRequestHeaders.Add("X-AgentCore-CSRF", token); }
    private static async Task<long> Revision(HttpClient client, Guid tenant) => (await Json(await client.GetAsync($"/api/v2/tenants/{tenant}/access/me"))).GetProperty("revision").GetInt64();
    private static async Task<JsonElement> Json(HttpResponseMessage response)
    { using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return json.RootElement.Clone(); }
}
