using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory(Timeout = 120000)] [InlineData(false, false, false)] [InlineData(true, false, false)] [InlineData(true, true, false)]
    [InlineData(false, false, true)] [InlineData(true, false, true)]
    public async Task Tenant_completion_read_Review_delivers_exact_child_then_consumes_only_in_same_task(bool sqlite, bool restart, bool childHandoff)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-completion-read-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        WebApplicationFactory<Program>? reopened = null; HttpClient? reopenedOwner = null;
        try
        {
            var model = new TenantCompletionModel(handoff: childHandoff, readReview: true);
            await using WebApplicationFactory<Program> initial = sqlite ? new TenantBackgroundSqliteFactory(root, model) : new TenantBackgroundMemoryFactory(root, model);
            var factory = initial;
            using var initialOwner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") }); var owner = initialOwner;
            var setup = await StartTenantBackground(factory, owner, submit: false, fileCapabilities: [ToolCatalog.BackgroundStart, ToolCatalog.ExecutionWait,
                ToolCatalog.BackgroundList, ToolCatalog.BackgroundInspect, ToolCatalog.BackgroundTake, ToolCatalog.BackgroundAcknowledge, ToolCatalog.WorkspaceRead]);
            var tenant = setup.Tenant; var api = $"/api/v2/tenants/{tenant}";
            var sessions = factory.Services.GetRequiredService<ITenantSessionStore>(); var runs = factory.Services.GetRequiredService<ITenantRunStore>();
            var runtime = factory.Services.GetRequiredService<ITenantRuntimeStore>(); var approvals = factory.Services.GetRequiredService<ITenantApprovalStore>();
            var parent = (await sessions.FindTenantSessionAsync(tenant, setup.Session))!;
            var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var access = factory.Services.GetRequiredService<TenantAccessService>();
            var role = await access.SaveRoleAsync(human, tenant, null, "Exact completion reader", ["sessions.read", "agent-runs.read", "agents.read", "workspaces.read"], await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, parent.Owner.ExecutorPrincipalId, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/agent-runs.read", new { mode = "always", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
            if (childHandoff)
            {
                Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsync(api + $"/agents/{parent.Owner.AgentInstanceId}/workspace/content?path=%2Fhome%2Finbox-evidence.txt&expectedRevision={await Revision(owner, tenant)}", new ByteArrayContent("Exact child evidence"u8.ToArray()))).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/workspaces.read", new { mode = "always", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
            }
            var id = (await Json(await owner.PostAsJsonAsync(api + $"/sessions/{setup.Session}/messages", new { messageId = Guid.NewGuid(), text = "Read the exact reviewed child result and use it in the final answer", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }))).GetProperty("runId").GetGuid();
            string Diagnostics() => string.Join(" | ", model.Requests.SelectMany(r => r.Messages).Where(m => m.Role == ModelRole.Tool)
                .Select(m => m.Name + ":" + m.Text).Distinct().TakeLast(8)) + " Logs: " + string.Join(" | ", model.Failures);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            TenantSession child; AgentRun finishedChild; var childDecided = false;
            while (true)
            {
                var children = (await sessions.ListTenantSessionsAsync(tenant, null, 0, 100)).Items.Where(s => s.Snapshot.Origin.OriginatingSessionId == setup.Session).ToArray();
                if (children.Length == 1)
                {
                    var leaf = (await runs.FindTenantRunAsync(tenant, children[0].Snapshot.Origin.InitialBackgroundAgentRunId!.Value))!;
                    while (leaf.Result?.ContinuationRunId is { } successor) leaf = (await runs.FindTenantRunAsync(tenant, successor))!;
                    if (childHandoff && !childDecided && leaf.Status == AgentRunStatus.WaitingForApproval)
                    {
                        var childRequest = (await approvals.FindApprovalAsync(tenant, leaf.ApprovalRequestId!.Value))!;
                        var childView = await Json(await owner.GetAsync(api + $"/approvals/{childRequest.RequestId}"));
                        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/approvals/{childRequest.RequestId}/decision", new { decision = "approve", actionHash = childRequest.Operation.ActionHash,
                            expectedRevision = childView.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = childRequest.Revision })).StatusCode);
                        childDecided = true;
                    }
                    if (leaf.IsTerminal) { Assert.Equal(AgentRunStatus.Completed, leaf.Status); child = children[0]; finishedChild = leaf; break; }
                }
                Assert.False(timeout.IsCancellationRequested, Diagnostics()); await Task.Delay(20);
            }
            model.ReadyForRead.TrySetResult();
            AgentRun waiting;
            while (true)
            {
                waiting = (await runs.FindTenantRunAsync(tenant, id))!;
                if (waiting.Status == AgentRunStatus.WaitingForApproval) break;
                Assert.False(waiting.IsTerminal || timeout.IsCancellationRequested, $"{waiting.Status} {waiting.Failure?.Code}; " + Diagnostics()); await Task.Delay(20);
            }
            var request = (await approvals.FindApprovalAsync(tenant, waiting.ApprovalRequestId!.Value))!;
            Assert.Equal(ApprovalKind.Review, request.Kind); Assert.NotNull(request.Operation.CompletionRead);
            Assert.Equal(child.Snapshot.Origin.InitialBackgroundAgentRunId, request.Operation.CompletionRead!.InitialRunId);
            Assert.Equal(finishedChild.AgentRunId, request.Operation.CompletionRead.SourceRunId);
            Assert.Equal(childHandoff, request.Operation.CompletionRead.SourceRunId != request.Operation.CompletionRead.InitialRunId);
            if (restart)
            {
                Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
                await initial.DisposeAsync(); model = new TenantCompletionModel(handoff: childHandoff, readReview: true);
                reopened = new TenantBackgroundSqliteFactory(root, model); factory = reopened;
                reopenedOwner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") }); owner = reopenedOwner;
                await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
                sessions = factory.Services.GetRequiredService<ITenantSessionStore>(); runs = factory.Services.GetRequiredService<ITenantRunStore>();
                runtime = factory.Services.GetRequiredService<ITenantRuntimeStore>(); approvals = factory.Services.GetRequiredService<ITenantApprovalStore>();
                Assert.Equal(TenantApprovalStatus.Pending, (await approvals.FindApprovalAsync(tenant, request.RequestId))!.Status);
            }
            var detail = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}"));
            Assert.Equal("inspect_background_completion", detail.GetProperty("preparedAction").GetProperty("operation").GetString());
            Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = "approve", actionHash = request.Operation.ActionHash,
                expectedRevision = detail.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = request.Revision })).StatusCode);
            while (!model.Acknowledged.Task.IsCompleted)
            {
                var original = (await runs.FindTenantRunAsync(tenant, id))!;
                var leaf = original.Result?.ContinuationRunId is { } next ? (await runs.FindTenantRunAsync(tenant, next))! : original;
                Assert.False(leaf.IsTerminal && leaf.Result?.OutcomeKind != AgentRunOutcomeKind.Continued || timeout.IsCancellationRequested,
                    $"{leaf.Status} {leaf.Failure?.Code}; " + Diagnostics()); await Task.Delay(20);
            }
            var handed = (await runs.FindTenantRunAsync(tenant, id))!;
            Assert.Equal(AgentRunOutcomeKind.Continued, handed.Result!.OutcomeKind);
            var consuming = (await runs.FindTenantRunAsync(tenant, handed.Result.ContinuationRunId!.Value))!;
            var source = (await runtime.ReadTenantBackgroundCompletionAsync(consuming.Owner, finishedChild.AgentRunId))!;
            Assert.Equal(CompletionInboxStatus.Claimed, source.Item!.Status); Assert.Equal(consuming.AgentRunId, source.Item.ClaimRunId); Assert.Null(source.Item.HandledByRunId);
            model.Finish.TrySetResult(); var final = await AwaitTenantWaitState(runs, tenant, consuming.AgentRunId, true);
            Assert.Equal(AgentRunStatus.Completed, final.Status);
            source = (await runtime.ReadTenantBackgroundCompletionAsync(final.Owner, source.Child.AgentRunId))!;
            Assert.Equal(CompletionInboxStatus.Handled, source.Item!.Status); Assert.Equal(final.AgentRunId, source.Item.HandledByRunId); Assert.Null(source.Item.ReportActivationId);
            Assert.Null(await approvals.FindOperationGrantAsync(tenant, request.RequestId)); Assert.Empty(model.Failures);
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { reopenedOwner?.Dispose(); if (reopened is not null) await reopened.DisposeAsync(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
