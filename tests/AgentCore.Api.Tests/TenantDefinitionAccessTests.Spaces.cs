using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantDefinitionAccessTests
{
    [Fact]
    public async Task Space_HTTP_provisions_private_and_exercises_membership_context_and_stale_edit_denial()
    {
        await using var factory = new AgentCoreApiFactory();
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var path = $"/api/v2/tenants/{tenant}/spaces";
        var createdAgent = await owner.PostAsJsonAsync($"/api/v2/tenants/{tenant}/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) });
        Assert.Equal(HttpStatusCode.Created, createdAgent.StatusCode);
        var agent = (await Json(createdAgent)).GetProperty("instance").GetProperty("instanceId").GetGuid();
        var privateSpace = await factory.Services.GetRequiredService<ITenantSpaceStore>().FindPrivateSpaceAsync(tenant, agent);
        Assert.NotNull(privateSpace);
        var privateMetadata = await Json(await owner.GetAsync($"/api/v2/tenants/{tenant}/agents/{agent}/private-space"));
        Assert.Equal(privateSpace.SpaceId, privateMetadata.GetProperty("spaceId").GetGuid());
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/v2/tenants/{tenant}/agents/{Guid.NewGuid()}/private-space")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"{path}/{privateSpace.SpaceId}")).StatusCode);
        var privateAccess = await Json(await owner.GetAsync($"{path}/{privateSpace.SpaceId}/access"));
        Assert.DoesNotContain(privateAccess.GetProperty("permissions").EnumerateArray(), p => p.GetString() == "spaces.delete");
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.DeleteAsync($"{path}/{privateSpace.SpaceId}?expectedRevision={await Revision(owner, tenant)}&expectedSpaceRevision=1")).StatusCode);
        var id = Guid.NewGuid();
        var created = await owner.PostAsJsonAsync(path, new { spaceId = id, name = "Release room", description = "Shared work", instructions = "Review changes", expectedRevision = await Revision(owner, tenant) });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var room = await Json(created); Assert.False(room.GetProperty("environmentsEnabled").GetBoolean());
        var membershipPath = $"{path}/{id}/memberships/{agent}";
        var added = await owner.PutAsJsonAsync(membershipPath, new { responsibility = "Reviewer", instructions = "Check invariants", disabledInheritedBindingKeys = Array.Empty<string>(), addedBindings = Array.Empty<object>(), expectedRevision = await Revision(owner, tenant), expectedSpaceRevision = 1, expectedMembershipRevision = 0 });
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var access = await Json(await owner.GetAsync($"{path}/{id}/access"));
        Assert.Contains(access.GetProperty("permissions").EnumerateArray(), p => p.GetString() == "space-memberships.manage");
        Assert.Equal(2, access.GetProperty("spaceRevision").GetInt64());
        var scopedAccess = await Json(await owner.GetAsync($"/api/v2/tenants/{tenant}/access/resources/space/{id}/editor"));
        Assert.True(scopedAccess.GetProperty("canAssign").GetBoolean()); Assert.Equal("Release room", scopedAccess.GetProperty("name").GetString());
        Assert.Contains(scopedAccess.GetProperty("principals").GetProperty("items").EnumerateArray(), p => p.GetProperty("kind").GetString() == "agent");
        var homeAccess = await Json(await owner.GetAsync($"/api/v2/tenants/{tenant}/access/resources/workspace/home:{id}/editor"));
        Assert.Equal("Release room · /home files", homeAccess.GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/v2/tenants/{tenant}/access/resources/space/{Guid.NewGuid()}/editor")).StatusCode);
        var bindings = await Json(await owner.GetAsync($"{path}/{id}/memberships/{agent}/bindings"));
        Assert.Equal(agent, bindings.GetProperty("membership").GetProperty("instanceId").GetGuid());
        Assert.Equal(2, bindings.GetProperty("spaceRevision").GetInt64());
        Assert.Equal("Check invariants", (await Json(await owner.GetAsync($"{path}/{id}/memberships"))).GetProperty("items")[0].GetProperty("instructions").GetString());
        var stale = await owner.PatchAsJsonAsync($"{path}/{id}", new { name = "Stale name", description = "", instructions = "", environmentsEnabled = false, expectedRevision = await Revision(owner, tenant), expectedSpaceRevision = 1 });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("Release room", (await Json(await owner.GetAsync($"{path}/{id}"))).GetProperty("name").GetString());
        var foreign = (await Json(await owner.PostAsJsonAsync("/api/v2/system/tenants", new { name = "Other", slug = "other-space" }))).GetProperty("tenantId").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/v2/tenants/{foreign}/spaces/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.DeleteAsync($"{path}/{id}?expectedRevision={await Revision(owner, tenant)}&expectedSpaceRevision=2")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"{membershipPath}?expectedRevision={await Revision(owner, tenant)}&expectedSpaceRevision=2&expectedMembershipRevision=1")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"{path}/{id}?expectedRevision={await Revision(owner, tenant)}&expectedSpaceRevision=3")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"{path}/{id}")).StatusCode);
    }
}
