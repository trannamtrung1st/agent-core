using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AgentCore.Api.Tests;

public sealed partial class TenantDefinitionAccessTests
{
    [Fact]
    public async Task Owned_instance_sparse_settings_budgets_model_and_self_management_use_current_authority()
    {
        await using var factory = new AgentCoreApiFactory();
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var prefix = $"/api/v2/tenants/{tenant}";
        var draft = await Json(await owner.PostAsJsonAsync($"{prefix}/agent-definitions", new { name = "Owned settings", expectedRevision = await Revision(owner, tenant) }));
        var definitionId = draft.GetProperty("definitionId").GetString(); var draftId = draft.GetProperty("draftId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"{prefix}/definition-drafts/{draftId}/publish", new { expectedRevision = await Revision(owner, tenant), expectedDraftRevision = 1 })).StatusCode);
        var created = await Json(await owner.PostAsJsonAsync($"{prefix}/agents/from-publication", new { definitionId, version = 1, expectedRevision = await Revision(owner, tenant) }));
        var id = created.GetProperty("instance").GetProperty("instanceId").GetGuid(); var path = $"{prefix}/agents/{id}";
        async Task<long> InstanceRevision() => (await Json(await owner.GetAsync(path))).GetProperty("instance").GetProperty("revision").GetInt64();
        async Task<HttpResponseMessage> Patch(string section, object set, string[]? clear = null, long? instanceRevision = null) => await owner.PatchAsJsonAsync($"{path}/settings/{section}", new { set, clear = clear ?? [], expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = instanceRevision ?? await InstanceRevision() });
        var initial = await Json(await owner.GetAsync($"{path}/settings"));
        var inherited = initial.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("section").GetString() == "instructions").GetProperty("effective").GetProperty("agentInstructions").GetString();
        Assert.Equal(HttpStatusCode.OK, (await Patch("instructions", new { agentInstructions = "Private Instance instruction override." })).StatusCode);
        Assert.Equal("Private Instance instruction override.", (await Json(await owner.GetAsync($"{path}/effective-config"))).GetProperty("definition").GetProperty("agentInstructions").GetString());
        var current = await Json(await owner.GetAsync($"{path}/settings"));
        Assert.Equal("instance", current.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("section").GetString() == "instructions").GetProperty("sources").GetProperty("agentInstructions").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await Patch("instructions", new { agentInstructions = "Stale write" }, instanceRevision: 1)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Patch("capabilities", new { selected = new[] { "system.grant_owner" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Patch("providerPreferences", new { languageModel = "unknown-provider" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Patch("instructions", new { tenantId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Patch("behaviorPolicy", new { avoidUnsupportedClaims = false })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Patch("instructions", new { agentInstructions = "Conflicting fields" }, ["agentInstructions"])).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Patch("sandboxPolicy", new { profileId = "global-profile" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Patch("instructions", new { }, ["agentInstructions"])).StatusCode);
        Assert.Equal(inherited, (await Json(await owner.GetAsync($"{path}/effective-config"))).GetProperty("definition").GetProperty("agentInstructions").GetString());
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"{path}/execution-budgets", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = await InstanceRevision(), budgets = new { standard = new { maxSteps = 8, durationSeconds = 60, perToolSeconds = 1, preset = 3, sandboxCommandSeconds = 5 } } })).StatusCode);
        var budget = await Json(await owner.GetAsync($"{path}/execution-budgets"));
        Assert.Equal("instance", budget.GetProperty("effective").GetProperty("Standard").GetProperty("source").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync($"{path}/execution-budgets", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = await InstanceRevision(), budgets = new { standard = new { maxSteps = 1000, durationSeconds = 60 } } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"{path}/execution-budgets", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = await InstanceRevision(), budgets = (object?)null })).StatusCode);
        Assert.NotEqual("instance", (await Json(await owner.GetAsync($"{path}/execution-budgets"))).GetProperty("effective").GetProperty("Standard").GetProperty("source").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync($"{path}/unattended-model", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = await InstanceRevision(), offeringId = "unavailable-model" })).StatusCode);
        var model = (await Json(await owner.GetAsync($"{prefix}/definition-authoring-options?definitionId={definitionId}"))).GetProperty("authoring").GetProperty("models")[0].GetProperty("offeringId").GetString();
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"{path}/unattended-model", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = await InstanceRevision(), offeringId = model, reasoningEffort = (string?)null })).StatusCode);
        Assert.Equal(model, (await Json(await owner.GetAsync(path))).GetProperty("instance").GetProperty("unattendedOfferingId").GetString());
        var policy = new { mode = new { value = "Assisted" }, scopes = new { value = new[] { "Settings" } }, configurationAssistance = new { value = false } };
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"{path}/self-management", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = await InstanceRevision(), overrides = policy })).StatusCode);
        var management = (await Json(await owner.GetAsync($"{path}/self-management"))).GetProperty("policy");
        Assert.Equal("Assisted", management.GetProperty("effective").GetProperty("mode").GetString());
        Assert.False(management.GetProperty("effective").GetProperty("builtInSkills").GetProperty("configurationAssistance").GetBoolean());
        var beforeInvalid = await Revision(owner, tenant); var beforeInvalidInstance = await InstanceRevision();
        foreach (var invalid in new[] { "{\"unknownArea\":true}", "{\"mode\":{\"value\":\"Managed\",\"unknown\":true}}", "{\"configurationAssistance\":{}}", "{\"scopes\":{\"value\":[\"Settings\",\"Settings\"]}}" })
        {
            using var parsed = JsonDocument.Parse(invalid);
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync($"{path}/self-management", new {
                expectedRevision = beforeInvalid, expectedInstanceRevision = beforeInvalidInstance, overrides = parsed.RootElement })).StatusCode);
            Assert.Equal(beforeInvalid, await Revision(owner, tenant)); Assert.Equal(beforeInvalidInstance, await InstanceRevision());
        }
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"{path}/self-management/freeze", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = await InstanceRevision() })).StatusCode);
        Assert.True((await Json(await owner.GetAsync($"{path}/self-management"))).GetProperty("policy").GetProperty("effective").GetProperty("frozen").GetBoolean());
        Assert.True((await Json(await owner.GetAsync($"{path}/effective-config"))).GetProperty("definition").GetProperty("selfManagement").GetProperty("frozen").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync($"{path}/self-management", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = await InstanceRevision(), overrides = (object?)null })).StatusCode);
        var beta = (await Json(await owner.PostAsJsonAsync("/api/v2/system/tenants", new { name = "Other settings tenant", slug = "other-settings" }))).GetProperty("tenantId").GetGuid();
        foreach (var suffix in new[] { "/settings", "/execution-budgets", "/self-management" }) Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/v2/tenants/{beta}/agents/{id}{suffix}")).StatusCode);
        owner.DefaultRequestHeaders.Remove("X-AgentCore-CSRF");
        Assert.Equal(HttpStatusCode.BadRequest, (await Patch("instructions", new { agentInstructions = "Missing CSRF" })).StatusCode);
    }
}
