using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Fact]
    public async Task Canonical_operation_policy_HTTP_uses_human_governance_and_cannot_weaken_Core_or_cross_tenant()
    {
        await using var factory = new AgentCoreApiFactory(); using var owner = Client(factory);
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var api = $"/api/v2/tenants/{tenant}";
        var actor = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
        var path = api + "/operation-policies/agents.update";
        var initial = await owner.GetAsync(path); Assert.Equal(HttpStatusCode.OK, initial.StatusCode); Assert.True(initial.Headers.CacheControl?.NoStore);
        var baseline = await Json(initial); Assert.Equal("mutation", baseline.GetProperty("approvalClass").GetString());
        Assert.Equal("whenRequesterLacksPermission", baseline.GetProperty("minimum").GetProperty("mode").GetString());
        Assert.Equal(0, baseline.GetProperty("policyRevision").GetInt64());
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync(path, new { mode = "none", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
        var witness = await Revision(owner, tenant);
        owner.DefaultRequestHeaders.Remove("X-AgentCore-CSRF");
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync(path, new { mode = "always", independentReview = true, expectedRevision = witness, expectedPolicyRevision = 0 })).StatusCode);
        await Csrf(owner);
        var saved = await owner.PutAsJsonAsync(path, new { mode = "always", independentReview = true, expectedRevision = witness, expectedPolicyRevision = 0,
            actorPrincipalId = Guid.NewGuid(), tenantId = Guid.NewGuid(), humanOnly = false });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode); Assert.True(saved.Headers.CacheControl?.NoStore);
        var body = await Json(saved); Assert.Equal(1, body.GetProperty("policyRevision").GetInt64());
        Assert.True(body.GetProperty("effective").GetProperty("independentReview").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync(path, new { mode = "always", independentReview = false, expectedRevision = witness, expectedPolicyRevision = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync(path, new { mode = "42", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync(api + "/operation-policies/system.tenants.manage")).StatusCode);
        var beta = (await Json(await owner.PostAsJsonAsync("/api/v2/system/tenants", new { name = "Beta Policy", slug = "beta-policy" }))).GetProperty("tenantId").GetGuid();
        var separate = await Json(await owner.GetAsync($"/api/v2/tenants/{beta}/operation-policies/agents.update"));
        Assert.Equal(0, separate.GetProperty("policyRevision").GetInt64());
        Assert.Equal("whenRequesterLacksPermission", separate.GetProperty("effective").GetProperty("mode").GetString());
        using var anonymous = Client(factory); using var legacy = RetiredOwnerHeader.CreateClient(factory);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await legacy.GetAsync(path)).StatusCode);
        var audit = await factory.Services.GetRequiredService<IIamStore>().ReadAuditAsync(tenant, 200);
        Assert.Equal(actor, Assert.Single(audit, a => a.Action == "tenant.operation_policy.update").ActorPrincipalId);
        var metadata = await Json(await owner.GetAsync(api + "/permissions"));
        Assert.Equal("mutation", metadata.GetProperty("items").EnumerateArray().Single(p => p.GetProperty("key").GetString() == "agents.update").GetProperty("approvalClass").GetString());
    }
}
