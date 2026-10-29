using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Workflows;
using AgentCore.Domain.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Fact]
    public async Task Formal_workflow_HTTP_authoring_publish_start_decision_and_inspection_use_real_host()
    {
        await using var factory = new AgentCoreApiFactory(); using var owner = Client(factory);
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var api = $"/api/v2/tenants/{tenant}"; var root = api + "/workflows";
        var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
        Assert.True((await Json(await owner.GetAsync(root + "/readiness"))).GetProperty("ready").GetBoolean());
        Assert.True((await Json(await owner.GetAsync(root + "/editor"))).GetProperty("canCreate").GetBoolean());
        Assert.Equal(JsonValueKind.Null, (await Json(await owner.GetAsync(root + "/requests/no-saved-command"))).ValueKind);
        Assert.Equal(JsonValueKind.Null, (await Json(await owner.GetAsync(api + "/workflow-executions/start-requests/no-saved-start"))).ValueKind);
        var workflow = Guid.NewGuid(); var graph = new WorkflowGraph(1, [
            new("start", "Start", WorkflowNodeKind.Start),
            new("review", "Human process sign-off", WorkflowNodeKind.HumanDecision, WorkflowRequirement.Global, Decision: new(human, "Accept the recorded process outcome?")),
            new("done", "Workflow completed", WorkflowNodeKind.EndSuccess), new("failed", "Process rejected", WorkflowNodeKind.EndFailure)
        ], [new("a", "start", "review"), new("b", "review", "done", "approve"), new("c", "review", "failed", "reject")], [], [WorkflowInvocationKind.HumanManual, WorkflowInvocationKind.AutomationOccurrence], []);
        var draft = new WorkflowDraftWrite("Release sign-off", "A closed authorized process", graph, false, 0, await Revision(owner, tenant), "http-draft");
        owner.DefaultRequestHeaders.Remove("X-AgentCore-CSRF");
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync(root + $"/{workflow}/draft", draft)).StatusCode); await Csrf(owner);
        await Json(await owner.PutAsJsonAsync(root + $"/{workflow}/draft", draft));
        Assert.Equal(0, (await Json(await owner.PostAsync(root + $"/{workflow}/validate", null))).GetProperty("diagnostics").GetArrayLength());
        var controls = await Json(await owner.GetAsync(root + $"/{workflow}/editor"));
        Assert.True(controls.GetProperty("canUpdate").GetBoolean()); Assert.True(controls.GetProperty("canPublish").GetBoolean()); Assert.False(controls.GetProperty("canStart").GetBoolean());
        var publish = new WorkflowPublish(1, await Revision(owner, tenant), "http-publish");
        await Json(await owner.PostAsJsonAsync(root + $"/{workflow}/publish", publish));
        Assert.True((await Json(await owner.GetAsync(root + $"/{workflow}/editor"))).GetProperty("canStart").GetBoolean());
        var published = await Json(await owner.GetAsync(root + $"/{workflow}/versions/1"));
        Assert.Equal(1, published.GetProperty("version").GetInt32());
        using var input = JsonDocument.Parse("{}"); var request = new WorkflowStart(1, input.RootElement, "http-start", await Revision(owner, tenant));
        var receipt = await Json(await owner.PostAsJsonAsync(root + $"/{workflow}/executions", request));
        var execution = receipt.GetProperty("executionId").GetGuid(); Assert.False(receipt.TryGetProperty("source", out _));
        Assert.Equal(execution, (await Json(await owner.PostAsJsonAsync(root + $"/{workflow}/executions", request))).GetProperty("executionId").GetGuid());
        var processes = factory.Services.GetRequiredService<TenantWorkflowExecutionService>();
        var path = api + $"/workflow-executions/{execution}";
        async Task<JsonElement> ObserveStatus(string expected)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            string? status = null;
            try
            {
                while (true)
                {
                    await processes.ReconcileAsync(4, deadline.Token);
                    var observed = await Json(await owner.GetAsync(path, deadline.Token));
                    status = observed.GetProperty("status").GetString();
                    if (status == expected) return observed;
                    await Task.Delay(25, deadline.Token);
                }
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            { throw new TimeoutException($"Workflow did not reach {expected}; last status={status}."); }
        }
        var view = await ObserveStatus("WaitingForHuman");
        Assert.Equal("WaitingForHuman", view.GetProperty("status").GetString());
        foreach (var field in new[] { "accountId", "bookmarkId", "operationId", "authenticationSessionId", "source", "inputsJson" }) Assert.False(view.TryGetProperty(field, out _));
        var step = view.GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("nodeId").GetString() == "review");
        Assert.True(step.GetProperty("canDecide").GetBoolean());
        await Json(await owner.PostAsJsonAsync(path + "/nodes/review/decision", new { approved = true, rationale = "Reviewed", requestKey = "http-decision",
            expectedRevision = view.GetProperty("revision").GetInt64(), expectedStepRevision = step.GetProperty("revision").GetInt64(), expectedSecurityRevision = await Revision(owner, tenant) }));
        // Real Elsa bookmark delivery can still be owned by its asynchronous worker.
        // Observe the required outcome rather than assuming eight immediate polls settle it.
        var complete = await ObserveStatus("Completed"); Assert.Equal("Completed", complete.GetProperty("status").GetString());
        Assert.Equal(1, complete.GetProperty("provenCount").GetInt32());
        Assert.False(complete.GetProperty("canCancel").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync("/api/workflow-definitions")).StatusCode);
        // The HTTP mapper must retain the absence of a source model for this typed destination.
        var agent = (await Json(await owner.GetAsync(api + "/agents"))).GetProperty("items")[0];
        var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid();
        var automation = await Json(await owner.PostAsJsonAsync(api + $"/agents/{instance}/automations", new {
            enabled = false, name = "Pinned process", instructions = "Start the saved formal process", requiresTools = false, requiresVision = false,
            modelPreference = (object?)null, schedule = new { kind = "oneShot", timeZone = "UTC", atUtc = DateTimeOffset.UtcNow.AddHours(1) },
            startWorkflow = new { workflowId = workflow, version = 1, graphHash = published.GetProperty("graph").GetProperty("hash").GetString(), inputs = new { } },
            expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = agent.GetProperty("instance").GetProperty("revision").GetInt64()
        }));
        Assert.Equal("startWorkflow", automation.GetProperty("executionTarget").GetString());
        Assert.Equal(workflow, automation.GetProperty("startWorkflow").GetProperty("workflowId").GetGuid());
        Assert.Equal(JsonValueKind.Null, automation.GetProperty("modelPreference").ValueKind);
        Assert.Equal(JsonValueKind.Null, automation.GetProperty("targetSessionId").ValueKind);
        using var anonymous = Client(factory); Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
    }
}
