using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AgentCore.Api.Tests;

public sealed partial class TenantAgentAccessTests
{
    [Fact]
    public async Task Editor_witnesses_fence_persona_and_independent_settings_writes()
    {
        await using var factory = new AgentCoreApiFactory();
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var root = $"/api/v2/tenants/{tenant}/agents";
        var created = await Json(await owner.PostAsJsonAsync(root, new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) }));
        var id = created.GetProperty("instance").GetProperty("instanceId").GetGuid(); root += $"/{id}";
        var editor = await Json(await owner.GetAsync(root + "/editor"));
        Assert.True(editor.GetProperty("canUpdate").GetBoolean());
        var sections = editor.GetProperty("settings").EnumerateArray().ToArray();
        Assert.DoesNotContain(sections, item => item.GetProperty("section").GetString() is "stateIsolationPolicy" or "memoryPolicy");
        var voice = sections.Single(item => item.GetProperty("section").GetString() == "voice");
        Assert.False(voice.GetProperty("effective").TryGetProperty("enabled", out _));
        Assert.True(voice.GetProperty("effective").TryGetProperty("voiceId", out _));
        var instance = editor.GetProperty("agent").GetProperty("instance");
        var persona = await owner.PutAsJsonAsync(root + "/persona", new { persona = new { name = "Scoped editor", role = "Guide", description = "A configuration test", tone = "Calm" }, expectedRevision = editor.GetProperty("securityRevision").GetInt64(), expectedInstanceRevision = instance.GetProperty("revision").GetInt64(), expectedPersonaRevision = instance.GetProperty("personaRevision").GetInt64() });
        Assert.Equal(HttpStatusCode.OK, persona.StatusCode);
        var stale = await owner.PatchAsJsonAsync(root + "/settings/conversationPolicy", new { expectedRevision = editor.GetProperty("securityRevision").GetInt64(), expectedInstanceRevision = instance.GetProperty("revision").GetInt64(), set = new { language = "fr-FR" }, clear = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        editor = await Json(await owner.GetAsync(root + "/editor")); instance = editor.GetProperty("agent").GetProperty("instance");
        var updated = await owner.PatchAsJsonAsync(root + "/settings/conversationPolicy", new { expectedRevision = editor.GetProperty("securityRevision").GetInt64(), expectedInstanceRevision = instance.GetProperty("revision").GetInt64(), set = new { language = "fr-FR" }, clear = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var saved = await Json(updated);
        Assert.Equal(editor.GetProperty("securityRevision").GetInt64() + 1, saved.GetProperty("securityRevision").GetInt64());
        var reset = await owner.PatchAsJsonAsync(root + "/settings/conversationPolicy", new { expectedRevision = saved.GetProperty("securityRevision").GetInt64(), expectedInstanceRevision = saved.GetProperty("instance").GetProperty("revision").GetInt64(), set = new { }, clear = new[] { "language" } });
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        editor = await Json(await owner.GetAsync(root + "/editor"));
        Assert.Equal("Scoped editor", editor.GetProperty("agent").GetProperty("instance").GetProperty("persona").GetProperty("name").GetString());
        var conversation = editor.GetProperty("settings").EnumerateArray().Single(section => section.GetProperty("section").GetString() == "conversationPolicy");
        Assert.False(conversation.GetProperty("overrides").TryGetProperty("language", out _));
        Assert.Equal("definition", conversation.GetProperty("sources").GetProperty("language").GetString());
    }
}
