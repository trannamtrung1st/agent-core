using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Fact]
    public async Task Canonical_approval_HTTP_projects_exact_receipt_after_trusted_local_execution_without_exposing_grant_authority()
    {
        await using var factory = new AgentCoreApiFactory(); using var owner = Client(factory);
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
        var principal = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
        var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) }));
        var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid();
        var policy = await owner.PutAsJsonAsync(api + $"/agents/{instance}/self-management", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1,
            overrides = new { mode = new { value = "Assisted" }, scopes = new { value = new[] { "Settings" } } } });
        Assert.Equal(HttpStatusCode.OK, policy.StatusCode);
        var session = await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 2 }));
        var id = session.GetProperty("sessionId").GetGuid();
        var admission = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{id}/messages", new { messageId = Guid.NewGuid(), text = "Save an exact approved persona", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }));
        var runId = admission.GetProperty("runId").GetGuid(); var agentStore = factory.Services.GetRequiredService<ITenantAgentStore>();
        var current = (await agentStore.FindTenantAgentAsync(tenant, instance))!;
        var approvals = factory.Services.GetRequiredService<TenantApprovalService>();
        var request = (await approvals.PreparePersonaAsync(new(tenant, runId), current.Instance.Persona with { Name = "Verified receipt persona" }, 1)).Request!;
        var path = api + $"/approvals/{request.RequestId}";
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(path + "/decision", new { decision = "approve", actionHash = request.Operation.ActionHash,
            expectedRevision = await Revision(owner, tenant), expectedRequestRevision = 1 })).StatusCode);
        await approvals.DispatchPendingAsync();
        // Exercise the internal effect boundary with an actual persisted domain claim; production tenant worker dispatch remains a separate gate.
        var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var queued = (await runs.FindTenantRunAsync(tenant, runId))!;
        var generation = Guid.NewGuid(); var at = factory.Services.GetRequiredService<TimeProvider>().GetUtcNow();
        var claimed = queued.TakeClaim(generation, at, at.AddMinutes(5));
        var memory = Assert.IsType<InMemoryMemoryStore>(factory.Services.GetRequiredService<IMemoryStore>());
        lock (memory.AdmissionGate) memory.AgentRuns.Runs[runId] = claimed;
        var result = await factory.Services.GetRequiredService<TenantApprovedOperationService>().ExecutePersonaAsync(new(tenant, runId), request.RequestId,
            request.Operation.OperationId, generation, request.Operation.ActionHash);
        var read = await owner.GetAsync(path); Assert.Equal(HttpStatusCode.OK, read.StatusCode); Assert.True(read.Headers.CacheControl?.NoStore);
        var view = await Json(read); Assert.Equal("approved", view.GetProperty("status").GetString());
        Assert.Equal(principal, view.GetProperty("decision").GetProperty("principalId").GetGuid());
        Assert.Equal("completed", view.GetProperty("operationResult").GetProperty("outcome").GetString());
        Assert.Equal(result.OperationId, view.GetProperty("operationResult").GetProperty("operationId").GetGuid());
        Assert.DoesNotContain("grantId", view.GetRawText()); Assert.DoesNotContain("effectIdempotencyKey", view.GetRawText());
        Assert.Equal("Verified receipt persona", (await agentStore.FindTenantAgentAsync(tenant, instance))!.Instance.Persona.Name);
        var list = await Json(await owner.GetAsync(api + "/approvals"));
        Assert.Equal("completed", list.GetProperty("items")[0].GetProperty("operationResult").GetProperty("outcome").GetString());
    }
}
