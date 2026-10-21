using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Fact]
    public async Task Canonical_durable_approval_HTTP_rechecks_human_CSRF_exact_action_and_revisions_without_a_chat_connection()
    {
        await using var factory = new AgentCoreApiFactory(); using var owner = Client(factory);
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var api = $"/api/v2/tenants/{tenant}";
        var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) }));
        var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid();
        var session = await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }));
        var sessionId = session.GetProperty("sessionId").GetGuid();
        var receipt = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{sessionId}/messages", new { messageId = Guid.NewGuid(), text = "Update the persona", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }));
        var current = (await factory.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!;
        var prepared = await factory.Services.GetRequiredService<TenantApprovalService>().PreparePersonaAsync(new(tenant, receipt.GetProperty("runId").GetGuid()), current.Instance.Persona with { Name = "HTTP reviewed persona" }, 1);
        var request = prepared.Request!; var route = api + $"/approvals/{request.RequestId}";
        using var reviewer = Client(factory); await Login(reviewer, "owner@example.test", "explicit-synthetic-test-password");
        var response = await reviewer.GetAsync(route); Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        var view = await Json(response); Assert.Equal("exceptionalGrant", view.GetProperty("kind").GetString()); Assert.Equal("pending", view.GetProperty("status").GetString());
        Assert.Equal(request.Operation.ActionHash, view.GetProperty("preparedAction").GetProperty("actionHash").GetString());
        Assert.False(view.TryGetProperty("canonicalActionJson", out _)); Assert.False(view.TryGetProperty("grantId", out _));
        var list = await Json(await reviewer.GetAsync(api + "/approvals")); Assert.Single(list.GetProperty("items").EnumerateArray());
        using var anonymous = Client(factory); using var legacy = RetiredOwnerHeader.CreateClient(factory);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await legacy.GetAsync(route)).StatusCode);
        var beta = (await Json(await owner.PostAsJsonAsync("/api/v2/system/tenants", new { name = "Other Approvals", slug = "other-approvals" }))).GetProperty("tenantId").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await reviewer.GetAsync($"/api/v2/tenants/{beta}/approvals/{request.RequestId}")).StatusCode);
        var witness = await Revision(reviewer, tenant);
        reviewer.DefaultRequestHeaders.Remove("X-AgentCore-CSRF");
        Assert.Equal(HttpStatusCode.BadRequest, (await reviewer.PostAsJsonAsync(route + "/decision", new { decision = "approve", actionHash = request.Operation.ActionHash, expectedRevision = witness, expectedRequestRevision = 1 })).StatusCode);
        await Csrf(reviewer);
        Assert.Equal(HttpStatusCode.BadRequest, (await reviewer.PostAsJsonAsync(route + "/decision", new { decision = "grant-role", actionHash = request.Operation.ActionHash, expectedRevision = witness, expectedRequestRevision = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await reviewer.PostAsJsonAsync(route + "/decision", new { decision = "approve", actionHash = new string('0', 64), expectedRevision = witness, expectedRequestRevision = 1 })).StatusCode);
        var decided = await reviewer.PostAsJsonAsync(route + "/decision", new { decision = "approve", actionHash = request.Operation.ActionHash, expectedRevision = witness, expectedRequestRevision = 1,
            principalId = Guid.NewGuid(), executorPrincipalId = Guid.NewGuid(), operationId = Guid.NewGuid(), preparedAction = "forged" });
        Assert.Equal(HttpStatusCode.OK, decided.StatusCode); Assert.True(decided.Headers.CacheControl?.NoStore);
        Assert.Equal("approved", (await Json(decided)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await reviewer.PostAsJsonAsync(route + "/decision", new { decision = "approve", actionHash = request.Operation.ActionHash, expectedRevision = await Revision(reviewer, tenant), expectedRequestRevision = 1 })).StatusCode);
        var savedGrant = await factory.Services.GetRequiredService<ITenantApprovalStore>().FindOperationGrantAsync(tenant, request.RequestId);
        Assert.NotNull(savedGrant); Assert.Equal(request.ExecutorPrincipalId, savedGrant.ExecutorPrincipalId); Assert.Equal(request.Operation.OperationId, savedGrant.OperationId);
        var unchanged = await factory.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance);
        Assert.Equal(current.Instance.Persona, unchanged!.Instance.Persona);
        var nextReceipt = await Json(await reviewer.PostAsJsonAsync(api + $"/sessions/{sessionId}/messages", new { messageId = Guid.NewGuid(), text = "Independent cancellation", expectedRevision = await Revision(reviewer, tenant), expectedSessionRevision = 2 }));
        var nextRequest = (await factory.Services.GetRequiredService<TenantApprovalService>().PreparePersonaAsync(new(tenant, nextReceipt.GetProperty("runId").GetGuid()),
            current.Instance.Persona with { Name = "HTTP cancelled persona" }, 1)).Request!;
        var cancelRoute = api + $"/approvals/{nextRequest.RequestId}/cancel"; var cancelWitness = await Revision(reviewer, tenant);
        Assert.Equal(HttpStatusCode.BadRequest, (await reviewer.PostAsJsonAsync(cancelRoute, new { actionHash = new string('x', 65), expectedRevision = cancelWitness, expectedRequestRevision = 1 })).StatusCode);
        reviewer.DefaultRequestHeaders.Remove("X-AgentCore-CSRF");
        Assert.Equal(HttpStatusCode.BadRequest, (await reviewer.PostAsJsonAsync(cancelRoute, new { actionHash = nextRequest.Operation.ActionHash, expectedRevision = cancelWitness, expectedRequestRevision = 1 })).StatusCode);
        await Csrf(reviewer);
        var cancelled = await reviewer.PostAsJsonAsync(cancelRoute, new { actionHash = nextRequest.Operation.ActionHash, expectedRevision = cancelWitness, expectedRequestRevision = 1 });
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode); Assert.Equal("cancelled", (await Json(cancelled)).GetProperty("status").GetString());
        Assert.Null(await factory.Services.GetRequiredService<ITenantApprovalStore>().FindOperationGrantAsync(tenant, nextRequest.RequestId));
    }
}
