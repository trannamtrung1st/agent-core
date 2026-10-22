using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AgentCore.Api.Tests;

public sealed partial class TenantAgentAccessTests
{
    public static IEnumerable<object[]> ResourceUnchangedHumanCases =>
        from operation in new[] { "content", "metadata", "local-choice", "inherited-reset", "inherited-choice" }
        from relogin in new[] { false, true }
        select new object[] { operation, relogin };

    [Theory]
    [MemberData(nameof(ResourceUnchangedHumanCases))]
    public async Task Unchanged_resource_HTTP_commands_still_require_the_original_authenticated_human(string operation, bool relogin)
    {
        await using var factory = new AgentReadFenceFactory();
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var prefix = $"/api/v2/tenants/{tenant}";
        var created = await Json(await owner.PostAsJsonAsync(prefix + "/agents", new { templateId = "customer-support", version = 3, expectedRevision = await Revision(owner, tenant) }));
        var id = created.GetProperty("instance").GetProperty("instanceId").GetGuid(); var path = prefix + $"/agents/{id}";
        byte[] bytes = [65, 66];
        var uploaded = await Json(await owner.PostAsJsonAsync(path + "/resources", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1,
            input = new { logicalPath = "unchanged.txt", kind = 2, mediaType = "text/plain", content = bytes, enabled = false } }));
        var resource = uploaded.GetProperty("resource");
        if (operation.StartsWith("inherited", StringComparison.Ordinal))
            resource = (await Json(await owner.GetAsync(path + "/resources"))).GetProperty("catalog").GetProperty("resources").EnumerateArray().First(r => r.GetProperty("origin").GetString() == "Definition");
        if (operation == "inherited-choice")
            resource = (await Json(await owner.PostAsJsonAsync(path + "/resources/" + Uri.EscapeDataString(resource.GetProperty("key").GetString()!) + "/enabled",
                new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 2, expectedResourceRevision = 0, enabled = true }))).GetProperty("resource");
        var key = resource.GetProperty("key").GetString()!;
        var before = await Json(await owner.GetAsync(path)); var parentRevision = before.GetProperty("instance").GetProperty("revision").GetInt64(); var revision = await Revision(owner, tenant);
        factory.CaptureAt = operation == "metadata" ? "bytes" : "configuration-snapshot";
        factory.AfterCapture = async () => {
            Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync("/api/v2/auth/logout", null)).StatusCode);
            if (relogin) await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        };
        HttpResponseMessage result;
        if (operation is "content" or "metadata")
            result = await owner.PostAsJsonAsync(path + "/resources", new { expectedRevision = revision, expectedInstanceRevision = parentRevision,
                input = new { resourceId = Guid.Parse(key[9..]), expectedResourceRevision = 1, logicalPath = "unchanged.txt", kind = 2, mediaType = "text/plain", content = operation == "content" ? bytes : null } });
        else
            result = await owner.PostAsJsonAsync(path + "/resources/" + Uri.EscapeDataString(key) + "/enabled", new { expectedRevision = revision, expectedInstanceRevision = parentRevision,
                expectedResourceRevision = resource.GetProperty("revision").GetInt64(), enabled = operation == "inherited-reset" ? (bool?)null : operation == "inherited-choice" });
        Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode); Assert.Null(factory.AfterCapture);
        Assert.Equal("initiator_unavailable", (await Json(result)).GetProperty("reasonCode").GetString());
        if (!relogin) await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        Assert.Equal(revision, await Revision(owner, tenant));
        Assert.Equal(before.GetProperty("instance").GetRawText(), (await Json(await owner.GetAsync(path))).GetProperty("instance").GetRawText());
    }
}
