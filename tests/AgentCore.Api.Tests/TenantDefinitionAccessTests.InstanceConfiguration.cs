using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantDefinitionAccessTests
{
    [Fact]
    public async Task Owned_instance_configuration_persona_and_version_adoption_are_scoped_and_revisioned()
    {
        await using var factory = new AgentCoreApiFactory();
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var prefix = $"/api/v2/tenants/{tenant}";
        var draft = await Json(await owner.PostAsJsonAsync($"{prefix}/agent-definitions", new { name = "Owned configuration", expectedRevision = await Revision(owner, tenant) }));
        var definition = draft.GetProperty("definitionId").GetString(); var draftId = draft.GetProperty("draftId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"{prefix}/definition-drafts/{draftId}/publish", new { expectedRevision = await Revision(owner, tenant), expectedDraftRevision = 1 })).StatusCode);
        var created = await Json(await owner.PostAsJsonAsync($"{prefix}/agents/from-publication", new { definitionId = definition, version = 1, expectedRevision = await Revision(owner, tenant) }));
        var id = created.GetProperty("instance").GetProperty("instanceId").GetGuid();
        var configuration = await owner.GetAsync($"{prefix}/agents/{id}/effective-config"); Assert.Equal(HttpStatusCode.OK, configuration.StatusCode);
        var original = await Json(configuration);
        Assert.Equal("TenantPublication", original.GetProperty("effective").GetProperty("definitionSource").GetString());
        Assert.False(original.GetProperty("effective").TryGetProperty("durableExecutionEligibility", out _));
        var initialInstructions = original.GetProperty("definition").GetProperty("agentInstructions").GetString();
        var invalidPersona = await owner.PutAsJsonAsync($"{prefix}/agents/{id}/persona", new { persona = new { name = "Missing tone", role = "Guide", description = "Incomplete" }, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1, expectedPersonaRevision = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, invalidPersona.StatusCode);
        var persona = new { name = "Local persona", role = "Guide", description = "Independent Instance identity", tone = "Clear" };
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"{prefix}/agents/{id}/persona", new { persona, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1, expectedPersonaRevision = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync($"{prefix}/agents/{id}/persona", new { persona, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 2, expectedPersonaRevision = 1 })).StatusCode);
        var fork = await Json(await owner.PostAsJsonAsync($"{prefix}/agent-definitions/{definition}/drafts", new { version = 1, expectedRevision = await Revision(owner, tenant) }));
        var forkId = fork.GetProperty("draftId").GetGuid(); var candidate = JsonNode.Parse(fork.GetProperty("candidate").GetRawText())!;
        candidate["agentInstructions"] = "New owned instructions, adopted explicitly.";
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"{prefix}/definition-drafts/{forkId}", new { candidate, expectedRevision = await Revision(owner, tenant), expectedDraftRevision = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"{prefix}/definition-drafts/{forkId}/publish", new { expectedRevision = await Revision(owner, tenant), expectedDraftRevision = 2 })).StatusCode);
        Assert.Equal(initialInstructions, (await Json(await owner.GetAsync($"{prefix}/agents/{id}/effective-config"))).GetProperty("definition").GetProperty("agentInstructions").GetString());
        var before = await Revision(owner, tenant);
        var adopted = await owner.PutAsJsonAsync($"{prefix}/agents/{id}/active-version", new { version = 2, expectedRevision = before, expectedInstanceRevision = 2 });
        Assert.Equal(HttpStatusCode.OK, adopted.StatusCode);
        var saved = await Json(await owner.GetAsync($"{prefix}/agents/{id}/effective-config"));
        Assert.Equal("New owned instructions, adopted explicitly.", saved.GetProperty("definition").GetProperty("agentInstructions").GetString());
        Assert.Equal("Local persona", saved.GetProperty("agent").GetProperty("instance").GetProperty("persona").GetProperty("name").GetString());
        Assert.Equal(3, saved.GetProperty("agent").GetProperty("instance").GetProperty("revision").GetInt64());
        Assert.Equal(before + 1, await Revision(owner, tenant));
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync($"{prefix}/agents/{id}/active-version", new { version = 1, expectedRevision = before, expectedInstanceRevision = 3 })).StatusCode);
        var beta = (await Json(await owner.PostAsJsonAsync("/api/v2/system/tenants", new { name = "Beta configuration", slug = "beta-configuration" }))).GetProperty("tenantId").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/v2/tenants/{beta}/agents/{id}/effective-config")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PutAsJsonAsync($"/api/v2/tenants/{beta}/agents/{id}/persona", new { persona, expectedRevision = await Revision(owner, beta), expectedInstanceRevision = 3, expectedPersonaRevision = 2 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"{prefix}/agent-definitions/{definition}/publications/2/deprecate", new { expectedRevision = await Revision(owner, tenant), expectedMetadataRevision = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PutAsJsonAsync($"{prefix}/agents/{id}/active-version", new { version = 2, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 3 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"{prefix}/agents/{id}/effective-config")).StatusCode);
        var legacy = factory.Services.GetRequiredService<IAgentInstanceStore>();
        await Assert.ThrowsAsync<AccessDeniedException>(() => legacy.UpdateWithExpectedRevisionAsync(new(id, 3, Persona: new("Bypass", "Guide", "Denied", "Clear"), ExpectedPersonaRevision: 2), DateTimeOffset.UtcNow).AsTask());
        owner.DefaultRequestHeaders.Remove("X-AgentCore-CSRF");
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync($"{prefix}/agents/{id}/persona", new { persona, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 3, expectedPersonaRevision = 2 })).StatusCode);
    }
}
