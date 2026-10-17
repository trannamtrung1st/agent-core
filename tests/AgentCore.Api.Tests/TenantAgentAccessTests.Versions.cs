using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AgentCore.Api.Tests;

public sealed partial class TenantAgentAccessTests
{
    [Fact]
    public async Task Version_catalog_and_preview_are_scoped_non_mutating_and_feed_explicit_stale_safe_adoption()
    {
        await using var factory = new AgentCoreApiFactory();
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var root = $"/api/v2/tenants/{tenant}/agents";
        var created = await Json(await owner.PostAsJsonAsync(root, new { templateId = "general-assistant", version = 21, expectedRevision = await Revision(owner, tenant) }));
        var id = created.GetProperty("instance").GetProperty("instanceId").GetGuid(); root += $"/{id}";
        var catalog = await Json(await owner.GetAsync(root + "/versions?pageSize=1"));
        Assert.Equal(1, catalog.GetProperty("items").GetArrayLength()); Assert.True(catalog.GetProperty("total").GetInt32() > 1);
        Assert.Equal("general-assistant", catalog.GetProperty("agent").GetProperty("instance").GetProperty("definitionId").GetString());
        Assert.True(catalog.GetProperty("canAdopt").GetBoolean());
        var before = await Revision(owner, tenant);
        var previewResponse = await owner.GetAsync(root + "/active-version/preview?version=22"); var preview = await Json(previewResponse);
        Assert.True(previewResponse.Headers.CacheControl!.NoStore);
        Assert.Equal(21, preview.GetProperty("preview").GetProperty("currentVersion").GetInt32()); Assert.Equal(22, preview.GetProperty("preview").GetProperty("targetVersion").GetInt32());
        Assert.Equal(before, await Revision(owner, tenant)); Assert.Equal(1, preview.GetProperty("preview").GetProperty("instanceRevision").GetInt64());
        Assert.Equal(HttpStatusCode.OK, (await owner.PatchAsJsonAsync(root + "/settings/conversationPolicy", new { expectedRevision = before, expectedInstanceRevision = 1, set = new { language = "fr-FR" }, clear = Array.Empty<string>() })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync(root + "/active-version", new { version = 22, expectedRevision = before, expectedInstanceRevision = 1 })).StatusCode);
        preview = await Json(await owner.GetAsync(root + "/active-version/preview?version=22"));
        var adopted = await Json(await owner.PutAsJsonAsync(root + "/active-version", new { version = 22, expectedRevision = preview.GetProperty("securityRevision").GetInt64(), expectedInstanceRevision = preview.GetProperty("preview").GetProperty("instanceRevision").GetInt64() }));
        Assert.Equal(22, adopted.GetProperty("instance").GetProperty("activeVersion").GetInt32());
        var settings = await Json(await owner.GetAsync(root + "/settings"));
        Assert.Equal("fr-FR", settings.GetProperty("items").EnumerateArray().Single(s => s.GetProperty("section").GetString() == "conversationPolicy").GetProperty("effective").GetProperty("language").GetString());
        foreach (var suffix in new[] { "/versions?page=-1", "/versions?pageSize=101", "/active-version/preview?version=0" }) Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync(root + suffix)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync(root + "/active-version/preview?version=999999")).StatusCode);
        var foreign = (await Json(await owner.PostAsJsonAsync("/api/v2/system/tenants", new { name = "Foreign version scope", slug = "foreign-version-scope" }))).GetProperty("tenantId").GetGuid();
        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        foreach (var suffix in new[] { "/versions", "/active-version/preview?version=22" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/v2/tenants/{foreign}/agents/{id}" + suffix)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(root + suffix)).StatusCode);
        }
    }
}
