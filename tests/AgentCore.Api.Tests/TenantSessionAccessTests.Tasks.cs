using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Security;
using AgentCore.Application.Tasks;
using AgentCore.Application.Ports;
using AgentCore.Domain.Security;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Fact]
    public async Task Task_HTTP_uses_real_human_closed_input_CSRF_exact_receipts_and_tenant_privacy()
    {
        await using var factory = new AgentCoreApiFactory(); using var owner = Client(factory);
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var api = $"/api/v2/tenants/{tenant}";
        foreach (var retiredVersion in new[] { 21, 34 })
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync(api + "/agents", new {
                templateId = "general-assistant", version = retiredVersion, expectedRevision = await Revision(owner, tenant)
            })).StatusCode);
        async Task<Guid> Agent() => (await Json(await owner.PostAsJsonAsync(api + "/agents", new {
            templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant)
        }))).GetProperty("instance").GetProperty("instanceId").GetGuid();
        var source = await Agent(); var worker = await Agent();
        var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new {
            retentionKind = "saved", agentInstanceId = source, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1
        }))).GetProperty("sessionId").GetGuid();
        var path = api + $"/sessions/{session}/tasks";
        object Request(long revision, string objective = "Compare the approved migration options") => new {
            objective, title = "Migration comparison", target = new { instanceId = worker }, requestKey = "human-assignment", expectedSecurityRevision = revision
        };
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync(path, Request(await Revision(owner, tenant)))).StatusCode);
        await Json(await owner.PutAsJsonAsync(api + $"/agents/{worker}/cooperation", new {
            cooperation = new { acceptTasks = true, acceptDirectMessages = false, allowDirectMessageWake = false, maxConcurrentTasks = 2 },
            expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1
        }));
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync(path, Request(await Revision(owner, tenant)))).StatusCode);
        var principal = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
        var executor = (await factory.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, worker))!.PrincipalId;
        var access = factory.Services.GetRequiredService<TenantAccessService>();
        var role = await access.SaveRoleAsync(principal, tenant, null, "Explicit Task worker execution",
            ["sessions.create", "agent-runs.create", "spaces.read", "agent-messages.read", "agents.read"], await Revision(owner, tenant));
        await access.AssignRoleAsync(principal, tenant, executor, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
        owner.DefaultRequestHeaders.Remove("X-AgentCore-CSRF");
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(path, Request(await Revision(owner, tenant)))).StatusCode);
        await Csrf(owner);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(path, new {
            objective = "Forgery", target = new { instanceId = worker }, requestKey = "forged", expectedSecurityRevision = await Revision(owner, tenant), sourceRunId = Guid.NewGuid()
        })).StatusCode);
        var uniqueRequest = System.Text.Json.JsonSerializer.Serialize(Request(await Revision(owner, tenant)));
        foreach (var duplicateBody in new[] { uniqueRequest[..^1] + ",\"objective\":\"Changed duplicate objective\"}",
            uniqueRequest[..^1] + ",\"expectedSecurityRevision\":1}" })
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsync(path, new StringContent(duplicateBody, System.Text.Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Empty((await Json(await owner.GetAsync(path))).GetProperty("items").EnumerateArray());
        var admissionResponse = await owner.PostAsJsonAsync(path, Request(await Revision(owner, tenant)));
        Assert.True(admissionResponse.IsSuccessStatusCode, await admissionResponse.Content.ReadAsStringAsync());
        var admitted = await Json(admissionResponse);
        var taskId = admitted.GetProperty("task").GetProperty("taskId").GetGuid();
        Assert.False(admitted.GetProperty("replayed").GetBoolean());
        var replay = await Json(await owner.PostAsJsonAsync(path, Request(1)));
        Assert.True(replay.GetProperty("replayed").GetBoolean()); Assert.Equal(taskId, replay.GetProperty("task").GetProperty("taskId").GetGuid());
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync(path, Request(await Revision(owner, tenant), "Different objective"))).StatusCode);
        var detailPath = api + $"/tasks/{taskId}";
        var detailResponse = await owner.GetAsync(detailPath); Assert.True(detailResponse.Headers.CacheControl?.NoStore);
        var detail = await Json(detailResponse);
        Assert.Equal(worker, detail.GetProperty("selectedTargetInstanceId").GetGuid());
        Assert.NotEqual(session, detail.GetProperty("workerSessionId").GetGuid());
        Assert.Equal("Human", detail.GetProperty("sourceActorKind").GetString());
        Assert.Equal(source, detail.GetProperty("sourceInstanceId").GetGuid());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("sourceRunId").ValueKind);
        foreach (var field in new[] { "humanCommandId", "executorPrincipalId", "configuration", "claim", "checkpoint" })
            Assert.False(detail.TryGetProperty(field, out _));
        var guidance = new { text = "Use the confirmed current schema", requestKey = "human-guidance", expectedSecurityRevision = await Revision(owner, tenant) };
        var guidanceResponse = await owner.PostAsJsonAsync(detailPath + "/messages", guidance);
        Assert.True(guidanceResponse.IsSuccessStatusCode, await guidanceResponse.Content.ReadAsStringAsync());
        var firstMessage = await Json(guidanceResponse);
        var duplicate = await Json(await owner.PostAsJsonAsync(detailPath + "/messages", guidance));
        Assert.Equal(firstMessage.GetProperty("messageId").GetGuid(), duplicate.GetProperty("messageId").GetGuid());
        Assert.True(duplicate.GetProperty("replayed").GetBoolean());
        var message = Assert.Single((await Json(await owner.GetAsync(detailPath + "/messages"))).GetProperty("items").EnumerateArray());
        Assert.Equal("Human", message.GetProperty("senderKind").GetString()); Assert.Equal("HumanGuidance", message.GetProperty("kind").GetString());
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await owner.PostAsJsonAsync(api + $"/agent-threads/{Guid.NewGuid()}/messages", guidance)).StatusCode);
        using var anonymous = Client(factory);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(detailPath)).StatusCode);
        var beta = (await Json(await owner.PostAsJsonAsync("/api/v2/system/tenants", new { name = "Task isolation", slug = "task-isolation" }))).GetProperty("tenantId").GetGuid();
        foreach (var id in new[] { taskId, Guid.NewGuid() })
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/v2/tenants/{beta}/tasks/{id}")).StatusCode);
        var cancel = new { requestKey = "cancel-once", expectedRevision = detail.GetProperty("revision").GetInt64(), expectedSecurityRevision = await Revision(owner, tenant) };
        await Json(await owner.PostAsJsonAsync(detailPath + "/cancel", cancel));
        Assert.True((await Json(await owner.PostAsJsonAsync(detailPath + "/cancel", cancel))).GetProperty("replayed").GetBoolean());
        Assert.True((await Json(await owner.GetAsync(detailPath + "/requests/cancel-once?kind=cancel"))).GetProperty("confirmed").GetBoolean());
        Assert.Single((await Json(await owner.GetAsync(path))).GetProperty("items").EnumerateArray());
        var taskStore = factory.Services.GetRequiredService<ITenantTaskStore>(); var cancelledTask = (await taskStore.FindTaskAsync(tenant, taskId))!;
        Assert.True(await taskStore.ReconcileTaskAsync(tenant, taskId, cancelledTask.Revision, Guid.NewGuid()));
        var cancelled = await Json(await owner.GetAsync(detailPath)); Assert.True(cancelled.GetProperty("canRetry").GetBoolean());
        var replacementResponse = await owner.PostAsJsonAsync(path, new { objective = "Deliberate replacement after confirmed cancellation", target = new { instanceId = worker },
            requestKey = "replacement-once", retryOfTaskId = taskId, expectedSecurityRevision = await Revision(owner, tenant) });
        Assert.True(replacementResponse.IsSuccessStatusCode, await replacementResponse.Content.ReadAsStringAsync());
        var replacementId = (await Json(replacementResponse)).GetProperty("task").GetProperty("taskId").GetGuid(); Assert.NotEqual(taskId, replacementId);
        Assert.Equal(taskId, (await Json(await owner.GetAsync(api + $"/tasks/{replacementId}"))).GetProperty("retryOfTaskId").GetGuid());
        Assert.Equal("Cancelled", (await Json(await owner.GetAsync(detailPath))).GetProperty("state").GetString());
        await Json(await owner.PutAsJsonAsync(api + "/operation-policies/tasks.delegate", new { mode = "always", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 }));
        var reviewedRequest = new { objective = "Exact human assignment requiring review", target = new { instanceId = worker }, requestKey = "human-review-once", expectedSecurityRevision = await Revision(owner, tenant) };
        var pendingReview = await Json(await owner.PostAsJsonAsync(path, reviewedRequest));
        Assert.False(pendingReview.TryGetProperty("task", out _)); Assert.Equal("pendingReview", pendingReview.GetProperty("admissionState").GetString());
        var approvalId = pendingReview.GetProperty("approvalRequestId").GetGuid(); Assert.True(pendingReview.GetProperty("canReadApproval").GetBoolean());
        var recovery = await Json(await owner.GetAsync(path + "/recovery?requestKey=human-review-once"));
        Assert.Equal(approvalId, recovery.GetProperty("approvalRequestId").GetGuid()); Assert.True(recovery.GetProperty("replayed").GetBoolean());
        var approvalPath = api + $"/approvals/{approvalId}"; var proposal = await Json(await owner.GetAsync(approvalPath));
        Assert.Equal(JsonValueKind.Null, proposal.GetProperty("runId").ValueKind); Assert.Equal(session, proposal.GetProperty("sessionId").GetGuid());
        Assert.Equal("delegate_task", proposal.GetProperty("preparedAction").GetProperty("operation").GetString());
        await Json(await owner.PostAsJsonAsync(approvalPath + "/decision", new { decision = "approve", actionHash = proposal.GetProperty("preparedAction").GetProperty("actionHash").GetString(),
            expectedRevision = await Revision(owner, tenant), expectedRequestRevision = proposal.GetProperty("requestRevision").GetInt64() }));
        await factory.Services.GetRequiredService<TenantApprovalService>().DispatchPendingAsync();
        var recorded = await Json(await owner.GetAsync(approvalPath));
        Assert.Equal(JsonValueKind.Null, recorded.GetProperty("runId").ValueKind); Assert.Equal(JsonValueKind.Null, recorded.GetProperty("operationResult").ValueKind);
        Assert.True(recorded.GetProperty("taskAdmissionOutcome").GetProperty("admitted").GetBoolean());
        var reviewedTaskId = recorded.GetProperty("taskAdmissionOutcome").GetProperty("taskAdmission").GetProperty("taskId").GetGuid();
        var reviewedTaskPath = api + $"/tasks/{reviewedTaskId}"; var reviewedView = await Json(await owner.GetAsync(reviewedTaskPath));
        Assert.Equal(JsonValueKind.Null, reviewedView.GetProperty("sourceRunId").ValueKind);
        await Json(await owner.PostAsJsonAsync(reviewedTaskPath + "/cancel", new { requestKey = "cancel-reviewed", expectedRevision = reviewedView.GetProperty("revision").GetInt64(), expectedSecurityRevision = await Revision(owner, tenant) }));
        var reviewedTask = (await taskStore.FindTaskAsync(tenant, reviewedTaskId))!;
        await taskStore.ReconcileTaskAsync(tenant, reviewedTaskId, reviewedTask.Revision, Guid.NewGuid());
        var recoveredAdmission = await Json(await owner.GetAsync(path + "/recovery?requestKey=human-review-once"));
        Assert.Equal(1, recoveredAdmission.GetProperty("task").GetProperty("revision").GetInt64());
        Assert.False(recoveredAdmission.GetProperty("task").TryGetProperty("state", out _));
        await Json(await owner.PutAsJsonAsync(api + "/operation-policies/tasks.delegate", new { mode = "whenrequesterlackspermission", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 1 }));
        object More(string requestKey, long revision) => new { objective = "Another independent bounded assignment", target = new { instanceId = worker }, requestKey,
            expectedSecurityRevision = revision };
        var second = await owner.PostAsJsonAsync(path, More("second-assignment", await Revision(owner, tenant))); Assert.True(second.IsSuccessStatusCode, await second.Content.ReadAsStringAsync());
        var full = await owner.PostAsJsonAsync(path, More("third-assignment", await Revision(owner, tenant))); Assert.Equal(HttpStatusCode.TooManyRequests, full.StatusCode);
        Assert.Equal("task_capacity_exceeded", (await Json(full)).GetProperty("reasonCode").GetString());
    }
}
