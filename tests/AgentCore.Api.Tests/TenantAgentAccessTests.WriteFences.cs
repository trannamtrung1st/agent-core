using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AgentCore.Api.Tests;

public sealed partial class TenantAgentAccessTests
{
    public static IEnumerable<object[]> AgentHumanWriteCases =>
        from operation in new[] { "create", "publication-create", "lifecycle", "persona", "version", "settings", "budgets", "model", "management", "freeze", "skill", "upload", "copy", "resource-choice", "resource-delete" }
        from relogin in new[] { false, true }
        select new object[] { operation, relogin };

    [Theory]
    [MemberData(nameof(AgentHumanWriteCases))]
    public async Task Every_agent_HTTP_mutation_carries_the_original_human_to_its_atomic_store(string operation, bool relogin)
    {
        await using var factory = new AgentReadFenceFactory();
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var prefix = $"/api/v2/tenants/{tenant}";
        var created = await Json(await owner.PostAsJsonAsync(prefix + "/agents", new { templateId = operation == "copy" ? "customer-support" : "examiner", version = operation == "copy" ? 3 : 1, expectedRevision = await Revision(owner, tenant) }));
        var id = created.GetProperty("instance").GetProperty("instanceId").GetGuid(); var root = prefix + $"/agents/{id}"; string? key = null; Guid? publication = null;
        if (operation is "resource-choice" or "resource-delete")
        {
            var file = await Json(await owner.PostAsJsonAsync(root + "/resources", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1,
                input = new { logicalPath = "original.txt", kind = 2, mediaType = "text/plain", content = System.Text.Encoding.UTF8.GetBytes("Retained resource.") } }));
            key = file.GetProperty("resource").GetProperty("key").GetString();
        }
        if (operation == "copy") key = (await Json(await owner.GetAsync(root + "/resources"))).GetProperty("catalog").GetProperty("resources")[0].GetProperty("key").GetString();
        if (operation == "freeze") Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(root + "/self-management", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1,
            overrides = new { mode = new { value = "Assisted" }, scopes = new { value = new[] { "Settings" } } } })).StatusCode);
        if (operation == "publication-create")
        {
            var draft = await Json(await owner.PostAsJsonAsync(prefix + "/agent-definitions", new { name = "Human-bound creation", expectedRevision = await Revision(owner, tenant) })); publication = draft.GetProperty("definitionId").GetGuid();
            Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(prefix + $"/definition-drafts/{draft.GetProperty("draftId").GetGuid()}/publish", new { expectedRevision = await Revision(owner, tenant), expectedDraftRevision = 1 })).StatusCode);
        }
        var before = await Json(await owner.GetAsync(root)); var instance = before.GetProperty("instance"); var revision = await Revision(owner, tenant);
        var inventory = await Json(await owner.GetAsync(prefix + "/agents")); var skills = await Json(await owner.GetAsync(root + "/skills")); var resources = await Json(await owner.GetAsync(root + "/resources"));
        var body = new Dictionary<string, object?> { ["expectedRevision"] = revision, ["expectedInstanceRevision"] = instance.GetProperty("revision").GetInt64() };
        var method = HttpMethod.Put; var path = root;
        switch (operation)
        {
            case "create": path = prefix + "/agents"; method = HttpMethod.Post; body["templateId"] = "examiner"; body["version"] = 1; break;
            case "publication-create": path = prefix + "/agents/from-publication"; method = HttpMethod.Post; body["definitionId"] = publication; body["version"] = 1; break;
            case "lifecycle": path += "/lifecycle"; body["lifecycle"] = 1; break;
            case "persona": path += "/persona"; body["expectedPersonaRevision"] = instance.GetProperty("personaRevision").GetInt64(); body["persona"] = new { name = "Protected update", role = "Guide", description = "Original human write", tone = "Calm" }; break;
            case "version": path += "/active-version"; body["version"] = 1; break;
            case "settings": path += "/settings/conversationPolicy"; method = HttpMethod.Patch; body["set"] = new { language = "fr-FR" }; body["clear"] = Array.Empty<string>(); break;
            case "budgets": path += "/execution-budgets"; body["budgets"] = null; break;
            case "model": path += "/unattended-model"; body["catalogKey"] = null; body["reasoningEffort"] = null; break;
            case "management": path += "/self-management"; body["overrides"] = new { mode = new { value = "Assisted" }, scopes = new { value = new[] { "Settings" } } }; break;
            case "freeze": path += "/self-management/freeze"; method = HttpMethod.Post; break;
            case "skill": path += "/skills/commands"; method = HttpMethod.Post; body["operation"] = "create"; body["input"] = new { id = "human-check", name = "Human check", description = "Check a scoped change", procedure = "Review the current task.", projection = "OnDemand", enabled = true, requiredCapabilities = new[] { "chat.respond" } }; break;
            case "upload": path += "/resources"; method = HttpMethod.Post; body["input"] = new { logicalPath = "protected.txt", kind = 2, mediaType = "text/plain", content = System.Text.Encoding.UTF8.GetBytes("Uncommitted binding sentinel.") }; break;
            case "copy": path += "/resources/" + Uri.EscapeDataString(key!) + "/copy"; method = HttpMethod.Post; body["logicalPath"] = "human-copy.txt"; break;
            case "resource-choice": path += "/resources/" + Uri.EscapeDataString(key!) + "/enabled"; method = HttpMethod.Post; body["expectedResourceRevision"] = 1; body["enabled"] = false; break;
            case "resource-delete": path += "/resources/" + Uri.EscapeDataString(key!) + $"?expectedRevision={revision}&expectedInstanceRevision={instance.GetProperty("revision").GetInt64()}&expectedResourceRevision=1"; method = HttpMethod.Delete; break;
        }
        factory.CaptureAt = operation is "create" or "publication-create" ? "creation-commit" : operation == "lifecycle" ? "lifecycle-commit" : "configuration-commit";
        factory.AfterCapture = async () =>
        {
            Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync("/api/v2/auth/logout", null)).StatusCode);
            if (relogin) await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        };
        Task<HttpResponseMessage> Send() => owner.SendAsync(new HttpRequestMessage(method, path) { Content = method == HttpMethod.Delete ? null : JsonContent.Create(body) });
        var denied = await Send(); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); Assert.Equal("initiator_unavailable", (await Json(denied)).GetProperty("reasonCode").GetString()); Assert.Null(factory.AfterCapture);
        if (!relogin) await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        Assert.Equal(revision, await Revision(owner, tenant));
        Assert.Equal(instance.GetRawText(), (await Json(await owner.GetAsync(root))).GetProperty("instance").GetRawText());
        Assert.Equal(inventory.GetRawText(), (await Json(await owner.GetAsync(prefix + "/agents"))).GetRawText());
        Assert.Equal(skills.GetRawText(), (await Json(await owner.GetAsync(root + "/skills"))).GetRawText()); Assert.Equal(resources.GetRawText(), (await Json(await owner.GetAsync(root + "/resources"))).GetRawText());
        var recovered = await Send(); Assert.True(recovered.IsSuccessStatusCode, await recovered.Content.ReadAsStringAsync());
        Assert.Equal(revision + 1, await Revision(owner, tenant));
        if (operation is "create" or "publication-create") Assert.Equal(inventory.GetProperty("total").GetInt32() + 1, (await Json(await owner.GetAsync(prefix + "/agents"))).GetProperty("total").GetInt32());
        else Assert.Equal(instance.GetProperty("revision").GetInt64() + 1, (await Json(await owner.GetAsync(root))).GetProperty("instance").GetProperty("revision").GetInt64());
    }

    [Fact]
    public async Task Noop_skill_reset_revalidates_original_human_without_a_commit_or_replay()
    {
        await using var factory = new AgentReadFenceFactory();
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var prefix = $"/api/v2/tenants/{tenant}";
        var created = await Json(await owner.PostAsJsonAsync(prefix + "/agents", new { templateId = "general-assistant", version = 32, expectedRevision = await Revision(owner, tenant) }));
        var id = created.GetProperty("instance").GetProperty("instanceId").GetGuid(); var root = prefix + $"/agents/{id}";
        var skills = await Json(await owner.GetAsync(root + "/skills"));
        var skill = skills.GetProperty("items").EnumerateArray().Single(s => s.GetProperty("key").GetString() == "definition:browser.record.lookup");
        var revision = await Revision(owner, tenant);
        var body = new { operation = "reset_enabled", key = skill.GetProperty("key").GetString(), expectedSkillRevision = skill.GetProperty("revision").GetInt64(), expectedRevision = revision, expectedInstanceRevision = 1 };
        factory.CaptureAt = "configuration-snapshot"; factory.AfterCapture = async () => Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync("/api/v2/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync(root + "/skills/commands", body)).StatusCode); Assert.Null(factory.AfterCapture);
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        Assert.Equal(revision, await Revision(owner, tenant));
        var recovered = await owner.PostAsJsonAsync(root + "/skills/commands", body); Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.False((await Json(recovered)).GetProperty("changed").GetBoolean()); Assert.Equal(revision, await Revision(owner, tenant));
    }

    [Fact]
    public async Task Agent_settings_write_rejects_original_session_logout_before_atomic_commit()
    {
        await using var factory = new AgentReadFenceFactory();
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var prefix = $"/api/v2/tenants/{tenant}";
        var created = await Json(await owner.PostAsJsonAsync(prefix + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) }));
        var id = created.GetProperty("instance").GetProperty("instanceId").GetGuid();
        var before = await Json(await owner.GetAsync(prefix + $"/agents/{id}/editor"));
        factory.CaptureAt = "definition"; factory.AfterCapture = async () => Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync("/api/v2/auth/logout", null)).StatusCode);
        var result = await owner.PatchAsJsonAsync(prefix + $"/agents/{id}/settings/conversationPolicy", new { expectedRevision = before.GetProperty("securityRevision").GetInt64(),
            expectedInstanceRevision = before.GetProperty("agent").GetProperty("instance").GetProperty("revision").GetInt64(), set = new { language = "fr-FR" }, clear = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
        Assert.Equal("initiator_unavailable", (await Json(result)).GetProperty("reasonCode").GetString());
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var after = await Json(await owner.GetAsync(prefix + $"/agents/{id}/editor"));
        Assert.Equal(before.GetProperty("securityRevision").GetInt64(), after.GetProperty("securityRevision").GetInt64());
        Assert.Equal(before.GetProperty("agent").GetProperty("instance").GetProperty("revision").GetInt64(), after.GetProperty("agent").GetProperty("instance").GetProperty("revision").GetInt64());
        var conversation = after.GetProperty("settings").EnumerateArray().Single(section => section.GetProperty("section").GetString() == "conversationPolicy");
        Assert.False(conversation.GetProperty("overrides").TryGetProperty("language", out _));
    }
}
