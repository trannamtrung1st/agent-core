using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using Microsoft.Extensions.DependencyInjection;
using AgentCore.Application.Tools;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Fact]
    public async Task Canonical_text_admission_uses_cookie_requester_and_actual_executor_and_keeps_tenant_runs_out_of_legacy_dispatch()
    {
        await using var factory = new AgentCoreApiFactory(); using var owner = Client(factory);
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var api = $"/api/v2/tenants/{tenant}";
        var requester = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
        var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) }));
        var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid();
        var executor = agent.GetProperty("principalId").GetGuid();
        var session = await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }));
        var id = session.GetProperty("sessionId").GetGuid(); var messageId = Guid.NewGuid();
        var route = api + $"/sessions/{id}/messages";
        var witness = await Revision(owner, tenant);
        owner.DefaultRequestHeaders.Remove("X-AgentCore-CSRF");
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(route, new { messageId, text = "Private request", expectedRevision = witness, expectedSessionRevision = 1 })).StatusCode);
        await Csrf(owner);
        var response = await owner.PostAsJsonAsync(route, new { messageId, text = "Private request", expectedRevision = witness, expectedSessionRevision = 1,
            tenantId = Guid.NewGuid(), agentPrincipalId = Guid.NewGuid(), originalInitiatorPrincipalId = executor, rootActivationId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        var receipt = await Json(response); var runId = receipt.GetProperty("runId").GetGuid();
        var read = await owner.GetAsync(api + $"/agent-runs/{runId}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode); Assert.True(read.Headers.CacheControl?.NoStore);
        var metadata = await Json(read); var context = metadata.GetProperty("context");
        Assert.Equal(requester, context.GetProperty("originalInitiatorPrincipalId").GetGuid());
        Assert.Equal(executor, context.GetProperty("agentPrincipalId").GetGuid()); Assert.Equal(tenant, context.GetProperty("tenantId").GetGuid());
        Assert.Equal("userMessage", context.GetProperty("origin").GetString()); Assert.Equal("queued", metadata.GetProperty("status").GetString());
        Assert.Equal(receipt.GetProperty("activationId").GetGuid(), context.GetProperty("rootActivationId").GetGuid());
        foreach (var field in new[] { "definition", "profileId", "agentInstructions", "entries", "summary", "checkpoint", "text" }) Assert.False(metadata.TryGetProperty(field, out _));
        Assert.DoesNotContain("Private request", metadata.GetRawText());
        var duplicate = await owner.PostAsJsonAsync(route, new { messageId, text = "Private request", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 2 });
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode); Assert.Equal(runId, (await Json(duplicate)).GetProperty("runId").GetGuid());
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync(route, new { messageId, text = "Changed request", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 2 })).StatusCode);
        var beta = (await Json(await owner.PostAsJsonAsync("/api/v2/system/tenants", new { name = "Beta Runs", slug = "beta-runs" }))).GetProperty("tenantId").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/v2/tenants/{beta}/agent-runs/{runId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync($"/api/v2/tenants/{beta}/sessions/{id}/messages", new { messageId = Guid.NewGuid(), text = "Foreign", expectedRevision = await Revision(owner, beta), expectedSessionRevision = 2 })).StatusCode);
        using var anonymous = Client(factory); using var legacy = RetiredOwnerHeader.CreateClient(factory);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(api + $"/agent-runs/{runId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await legacy.GetAsync(api + $"/agent-runs/{runId}")).StatusCode);
        var stored = (await factory.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(tenant, runId))!;
        Assert.Null(stored.Owner.ProfileIdOrNull); Assert.Equal(AgentRunStatus.Queued, stored.Status);
        using (var unframed = JsonDocument.Parse((await factory.Services.GetRequiredService<SessionToolExecutor>().ExecuteAsync(stored.Admission.Configuration!.Definition,
            stored.SessionId, new("unframed", ToolCatalog.CredentialsList, "{}"), 2000)).Text))
            Assert.Equal("tenant_context_required", unframed.RootElement.GetProperty("reasonCode").GetString());
        Assert.DoesNotContain(await factory.Services.GetRequiredService<IAgentRunStore>().ListRunnableAsync(DateTimeOffset.UtcNow, 100), r => r.AgentRunId == runId);
        var cancelRoute = api + $"/agent-runs/{runId}/cancel";
        var securityRevision = await Revision(owner, tenant);
        owner.DefaultRequestHeaders.Remove("X-AgentCore-CSRF");
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(cancelRoute, new { expectedRevision = securityRevision, expectedRunRevision = 1 })).StatusCode);
        await Csrf(owner);
        var cancelled = await owner.PostAsJsonAsync(cancelRoute, new { expectedRevision = securityRevision, expectedRunRevision = 1 });
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode); Assert.Equal("cancelled", (await Json(cancelled)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync(cancelRoute, new { expectedRevision = securityRevision, expectedRunRevision = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync(api + $"/sessions/{id}?expectedRevision={await Revision(owner, tenant)}&expectedSessionRevision=2")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync(api + $"/agent-runs/{runId}")).StatusCode);
    }
}
