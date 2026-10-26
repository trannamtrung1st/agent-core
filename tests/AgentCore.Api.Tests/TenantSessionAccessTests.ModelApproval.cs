using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory]
    [InlineData("approve")] [InlineData("reject")] [InlineData("cancel")]
    public async Task Model_prepares_durable_persona_approval_releases_Runtime_then_worker_consumes_exact_decision_and_resumes_without_reasking(string decision)
    {
        await using var factory = new ApprovalExecutionFactory(); using var owner = Client(factory);
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentCore.Api.Realtime.AgentCoreOptions>>().Value.MaxActiveSessions = 1;
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
        var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) }));
        var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + $"/agents/{instance}/self-management", new
        {
            expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1,
            overrides = new { mode = new { value = "Assisted" }, scopes = new { value = new[] { "Settings" } } }
        })).StatusCode);
        var agents = factory.Services.GetRequiredService<ITenantAgentStore>(); var before = (await agents.FindTenantAgentAsync(tenant, instance))!;
        factory.Model.ProposedPersona = before.Instance.Persona with { Name = "Model-requested exact persona" };
        var session = await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 2 }));
        var sessionId = session.GetProperty("sessionId").GetGuid();
        var submitted = await owner.PostAsJsonAsync(api + $"/sessions/{sessionId}/messages", new { messageId = Guid.NewGuid(), text = "Update your persona after the required approval", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 });
        Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode); var runId = (await Json(submitted)).GetProperty("runId").GetGuid();
        Volatile.Write(ref factory.Enabled, true);
        var runs = factory.Services.GetRequiredService<ITenantRunStore>(); AgentRun? waiting = null;
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            while (!deadline.IsCancellationRequested)
            {
                waiting = await runs.FindTenantRunAsync(tenant, runId);
                if (waiting?.Status == AgentRunStatus.WaitingForApproval) break;
                if (waiting?.IsTerminal == true) break;
                await Task.Delay(25, deadline.Token);
            }
        Assert.NotNull(waiting); Assert.True(waiting.Status == AgentRunStatus.WaitingForApproval, $"{waiting.Status}: {waiting.Failure?.Code}");
        Assert.Null(waiting.Claim); Assert.NotNull(waiting.ApprovalRequestId); Assert.Null(waiting.Checkpoint!.BudgetRecordedAtUtc);
        Assert.Equal(before.Instance.Persona, (await agents.FindTenantAgentAsync(tenant, instance))!.Instance.Persona);
        var request = (await factory.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, waiting.ApprovalRequestId!.Value))!;
        Assert.Equal(factory.Model.ProposedPersona, request.Operation.Persona);
        Assert.Equal(waiting.Admission.ExecutionContext!.RootActivationId, request.RootActivationId);
        Assert.Contains(factory.Model.Requests, r => r.Tools?.Any(t => t.Name == ToolCatalog.RequestAuthorization) == true);
        var capacity = factory.Services.GetRequiredService<AgentCore.Api.Realtime.TenantRuntimeCapacity>(); var released = false;
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            while (!deadline.IsCancellationRequested)
            {
                if (capacity.TryReserveTenantRuntime()) { released = true; capacity.ReleaseTenantRuntime(); break; }
                await Task.Delay(25, deadline.Token);
            }
        Assert.True(released, "The pending approval must release the sole Runtime slot before a decision.");
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/" + (decision == "cancel" ? "cancel" : "decision"), new
        {
            decision, actionHash = request.Operation.ActionHash, expectedRevision = await Revision(owner, tenant), expectedRequestRevision = request.Revision
        })).StatusCode);
        AgentRun? completed = null;
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            while (!deadline.IsCancellationRequested)
            {
                completed = await CurrentApprovalExecution(runs, tenant, runId);
                if (completed?.IsTerminal == true) break;
                await Task.Delay(25, deadline.Token);
            }
        Assert.NotNull(completed); Assert.True(completed.Status == AgentRunStatus.Completed, $"{completed.Status}: {completed.Failure?.Code}: {completed.Failure?.Summary}");
        Assert.Equal(decision == "approve" ? factory.Model.ProposedPersona : before.Instance.Persona, (await agents.FindTenantAgentAsync(tenant, instance))!.Instance.Persona);
        var grant = await factory.Services.GetRequiredService<ITenantApprovalStore>().FindOperationGrantAsync(tenant, request.RequestId);
        if (decision == "approve") Assert.Equal(OperationGrantStatus.Consumed, grant!.Status); else Assert.Null(grant);
        Assert.Single(await factory.Services.GetRequiredService<IIamStore>().ReadAuditAsync(tenant, 200), a => a.Action == "approvals.request");
        Assert.Equal(decision == "approve" ? 1 : 0, (await factory.Services.GetRequiredService<IIamStore>().ReadAuditAsync(tenant, 200)).Count(a => a.Action == "operations.persona.apply"));
        Assert.True(AgentCore.Application.Execution.AgentRunToolCallCheckpoint.TryRead(completed.Checkpoint, out var messages));
        Assert.DoesNotContain(AgentCore.Application.Execution.AgentRunToolCallCheckpoint.PendingCalls(messages!), c => c.Name == ToolCatalog.RequestAuthorization);
        var reason = decision switch { "approve" => "operation_completed", "reject" => "approval_rejected", _ => "approval_cancelled" };
        Assert.Contains(messages!, m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.RequestAuthorization && m.Text.Contains(reason));
    }
}
