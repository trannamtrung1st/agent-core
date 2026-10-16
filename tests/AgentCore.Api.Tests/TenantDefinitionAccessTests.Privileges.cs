using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AgentCore.Api.Tests;

public sealed partial class TenantDefinitionAccessTests
{
    [Fact]
    public async Task Mutation_only_roles_cannot_receive_stored_definition_or_instance_payloads()
    {
        await using var factory = new AgentCoreApiFactory();
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var draft = await Json(await owner.PostAsJsonAsync($"/api/v2/tenants/{tenant}/agent-definitions", new { name = "Private definition", expectedRevision = await Revision(owner, tenant) }));
        var definitionId = draft.GetProperty("definitionId").GetString(); var draftId = draft.GetProperty("draftId").GetGuid();
        var agent = await Json(await owner.PostAsJsonAsync($"/api/v2/tenants/{tenant}/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) }));
        var instanceId = agent.GetProperty("instance").GetProperty("instanceId").GetGuid();
        var role = await Json(await owner.PostAsJsonAsync($"/api/v2/tenants/{tenant}/roles", new { name = "Mutation without read", permissions = new[] { "agent-definitions.update", "agent-definitions.publish", "agents.update" }, expectedRevision = await Revision(owner, tenant) }));
        var invite = await Json(await owner.PostAsJsonAsync($"/api/v2/tenants/{tenant}/members/invite", new { login = "mutation-only@example.test", roleId = role.GetProperty("roleId").GetGuid(), expectedRevision = await Revision(owner, tenant) }));
        using var actor = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await Csrf(actor);
        Assert.Equal(HttpStatusCode.Created, (await actor.PostAsJsonAsync("/api/v2/auth/register", new { login = "mutation-only@example.test", displayName = "Mutation only", password = "strong-mutation-password",
            invitation = new { tenantId = tenant, invitationId = invite.GetProperty("invitationId").GetGuid(), token = invite.GetProperty("token").GetString() } })).StatusCode);
        await Login(actor, "mutation-only@example.test", "strong-mutation-password");
        var principal = (await Json(await actor.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("principalId").GetGuid();
        Assert.True((await Json(await actor.PostAsJsonAsync($"/api/v2/tenants/{tenant}/access/check", new { principalId = principal, permission = "agent-definitions.publish", resourceType = "definition", resourceId = definitionId }))).GetProperty("allowed").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await actor.PostAsJsonAsync($"/api/v2/tenants/{tenant}/definition-drafts/{draftId}/publish", new { expectedRevision = await Revision(owner, tenant), expectedDraftRevision = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await actor.PutAsJsonAsync($"/api/v2/tenants/{tenant}/definition-drafts/{draftId}", new { candidate = draft.GetProperty("candidate"), expectedRevision = await Revision(owner, tenant), expectedDraftRevision = 1 })).StatusCode);
        Assert.True((await Json(await actor.PostAsJsonAsync($"/api/v2/tenants/{tenant}/access/check", new { principalId = principal, permission = "agents.update", resourceType = "agent", resourceId = instanceId }))).GetProperty("allowed").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await actor.PutAsJsonAsync($"/api/v2/tenants/{tenant}/agents/{instanceId}/lifecycle", new { lifecycle = 1, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 })).StatusCode);
        Assert.Equal("Active", (await Json(await owner.GetAsync($"/api/v2/tenants/{tenant}/agents/{instanceId}"))).GetProperty("instance").GetProperty("lifecycle").GetString());
    }
}
