using System.Net;
using System.Net.Http.Json;
using AgentCore.Contracts.Http;

namespace AgentCore.Api.Tests;

public sealed partial class TenantCredentialAccessTests
{
    [Fact]
    public async Task Sandbox_catalog_HTTP_saves_exact_revisions_and_masks_foreign_and_global_sources()
    {
        await using var factory = new AgentCoreApiFactory();
        using var owner = Client(factory); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var path = $"/api/v2/tenants/{tenant}/sandbox-profiles";
        var input = new TenantSandboxProfileSaveRequest(await Revision(owner, tenant), 0, "Private image", "Synthetic catalog fixture", true,
            "mcr.microsoft.com/dotnet/sdk@sha256:" + new string('a', 64));
        var response = await owner.PostAsJsonAsync(path, input); Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore); var created = await Json(response); Safe(created);
        var p = created.GetProperty("profile"); var id = p.GetProperty("id").GetString();
        Assert.Equal("NeedsVerification", p.GetProperty("availability").GetString());
        Assert.Equal(1, p.GetProperty("revision").GetInt64()); Assert.True(created.GetProperty("canManage").GetBoolean());
        using var anonymous = Client(factory); Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        using var legacy = TestOwnerCapability.CreateOwnerClient(factory);
        Assert.Equal(HttpStatusCode.Unauthorized, (await legacy.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await legacy.GetAsync($"/api/v2/admin/sandbox-profiles/{id}")).StatusCode);
        Assert.DoesNotContain(id!, await legacy.GetStringAsync("/api/v2/admin/sandboxes"));
        var beta = (await Json(await owner.PostAsJsonAsync("/api/v2/system/tenants", new { name = "Beta", slug = "sandbox-beta" }))).GetProperty("tenantId").GetGuid();
        Assert.Equal(0, (await Json(await owner.GetAsync($"/api/v2/tenants/{beta}/sandbox-profiles"))).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/v2/tenants/{beta}/sandbox-profiles/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync(path + "/" + id, input with { ExpectedRevision = await Revision(owner, tenant), ExpectedProfileRevision = 7 })).StatusCode);
        var disabled = input with { ExpectedRevision = await Revision(owner, tenant), ExpectedProfileRevision = 1, Name = "Disabled fixture", Enabled = false };
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(path + "/" + id, disabled)).StatusCode);
        Assert.Equal("Private image", (await Json(await owner.GetAsync(path + "/" + id + "?revision=1"))).GetProperty("profile").GetProperty("name").GetString());
        var current = (await Json(await owner.GetAsync(path + "/" + id))).GetProperty("profile");
        Assert.Equal(2, current.GetProperty("revision").GetInt64()); Assert.Equal("Disabled", current.GetProperty("availability").GetString());
        var search = await Json(await owner.GetAsync(path + "?pageSize=1&search=Disabled")); Assert.Equal(1, search.GetProperty("total").GetInt32());
        var before = await Revision(owner, tenant);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(path, input with { ExpectedRevision = before, MemoryMiB = 100_000 })).StatusCode);
        Assert.Equal(before, await Revision(owner, tenant));
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(path, new { input.ExpectedRevision, input.ExpectedProfileRevision,
            input.Name, input.Description, input.Enabled, input.ImageReference, tenantId = beta })).StatusCode);
        Assert.Equal(before, await Revision(owner, tenant));
    }

    [Fact]
    public async Task Sandbox_catalog_HTTP_exact_profile_grants_do_not_create_siblings_or_authorize_execution()
    {
        await using var factory = new AgentCoreApiFactory();
        using var owner = Client(factory); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var root = $"/api/v2/tenants/{tenant}"; var path = root + "/sandbox-profiles";
        var catalog = (await Json(await owner.GetAsync(root + "/permissions"))).GetProperty("items");
        var read = catalog.EnumerateArray().Single(p => p.GetProperty("key").GetString() == "sandbox.read");
        Assert.Contains(read.GetProperty("resourceTypes").EnumerateArray(), x => x.GetString() == "sandbox-profile");
        var execute = catalog.EnumerateArray().Single(p => p.GetProperty("key").GetString() == "sandbox.execute");
        Assert.DoesNotContain(execute.GetProperty("resourceTypes").EnumerateArray(), x => x.GetString() == "sandbox-profile");
        var input = new TenantSandboxProfileSaveRequest(await Revision(owner, tenant), 0, "Exact profile", "Synthetic", true, "ghcr.io/acme/tool@sha256:" + new string('a', 64));
        var first = await Json(await owner.PostAsJsonAsync(path, input)); var id = first.GetProperty("profile").GetProperty("id").GetString();
        await Json(await owner.PostAsJsonAsync(path, input with { ExpectedRevision = await Revision(owner, tenant), Name = "Hidden profile" }));
        var baseRole = await Json(await owner.PostAsJsonAsync(root + "/roles", new { name = "Minimal member", permissions = new[] { "agents.chat" }, expectedRevision = await Revision(owner, tenant) }));
        var invitation = await Json(await owner.PostAsJsonAsync(root + "/members/invite", new { login = "sandbox-reader@example.test", roleId = baseRole.GetProperty("roleId").GetGuid(), expectedRevision = await Revision(owner, tenant) }));
        using var reader = Client(factory); await Csrf(reader);
        Assert.Equal(HttpStatusCode.Created, (await reader.PostAsJsonAsync("/api/v2/auth/register", new { login = "sandbox-reader@example.test", displayName = "Exact reader", password = "strong-reader-password",
            invitation = new { tenantId = tenant, invitationId = invitation.GetProperty("invitationId").GetGuid(), token = invitation.GetProperty("token").GetString() } })).StatusCode);
        await Login(reader, "sandbox-reader@example.test", "strong-reader-password");
        var user = (await Json(await owner.GetAsync(root + "/members"))).GetProperty("items").EnumerateArray().Single(m => m.GetProperty("login").GetString() == "SANDBOX-READER@EXAMPLE.TEST");
        var role = await Json(await owner.PostAsJsonAsync(root + "/roles", new { name = "One profile", permissions = new[] { "sandbox.read", "sandbox.manage" }, expectedRevision = await Revision(owner, tenant) }));
        var grant = await Json(await owner.PostAsJsonAsync(root + "/role-assignments", new { principalId = user.GetProperty("principalId").GetGuid(), roleId = role.GetProperty("roleId").GetGuid(), resourceType = "sandbox-profile", resourceId = id, expectedRevision = await Revision(owner, tenant) }));
        var visible = await Json(await reader.GetAsync(path + "?pageSize=1")); Assert.Equal(1, visible.GetProperty("total").GetInt32()); Assert.False(visible.GetProperty("canCreate").GetBoolean());
        Assert.Equal(id, visible.GetProperty("items")[0].GetProperty("profile").GetProperty("id").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync(path, input with { ExpectedRevision = await Revision(owner, tenant) })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reader.PutAsJsonAsync(path + "/" + id, input with { ExpectedRevision = await Revision(owner, tenant), ExpectedProfileRevision = 1, Name = "Exact change" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync(root + $"/role-assignments/{grant.GetProperty("assignmentId").GetGuid()}?expectedRevision={await Revision(owner, tenant)}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(path + "/" + id)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync(path + "/" + id, input with { ExpectedProfileRevision = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync(path + "/" + id, input with { ExpectedProfileRevision = 0, ExpectedRevision = 0 })).StatusCode);
        Assert.Equal(0, (await Json(await reader.GetAsync(path))).GetProperty("total").GetInt32());
    }
}
