using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    private sealed class TenantWaitModel(string mode) : TenantBackgroundModel
    {
        public TaskCompletionSource SuccessorEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSuccessor { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Enqueue(request); await Task.Yield(); cancellationToken.ThrowIfCancellationRequested();
            var child = Origin(request) == ExecutionOrigin.ImmediateBackground;
            var results = request.Messages.Where(m => m.Role == ModelRole.Tool).ToArray();
            var wait = results.LastOrDefault(m => m.Name == ToolCatalog.ExecutionWait);
            ModelToolCall? call = null;
            if (child)
            {
                Assert.Contains(request.Tools!, t => t.Name == ToolCatalog.ExecutionWait);
                if (mode == "handoff" && wait is not null)
                {
                    if (!results.Any(m => m.Name == ToolCatalog.RequestAuthorization))
                    {
                        yield return new ModelToolCallEvent(new("child-read", ToolCatalog.RequestAuthorization,
                            """{"operation":"inspect_workspace","read":{"tool":"workspace.read","arguments":{"path":"/home/wait-source.txt"}}}"""));
                        yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
                    }
                    Assert.Contains("\"readAuthorized\":true", results.Last(m => m.Name == ToolCatalog.RequestAuthorization).Text);
                    SuccessorEntered.TrySetResult(); await ReleaseSuccessor.Task.WaitAsync(cancellationToken);
                }
                call = wait is null ? new("child-wait", ToolCatalog.ExecutionWait, """{"mode":"duration","seconds":2}""")
                    : new("child-finish", ToolCatalog.WorkComplete, """{"outcome":"Response","summary":"Child finished after its same-Run wait.","attentionRequired":false}""");
            }
            else if (wait is null)
            {
                Assert.Contains(request.Tools!, t => t.Name == ToolCatalog.ExecutionWait);
                if (mode is "background" or "timeout" or "handoff")
                {
                    var started = results.LastOrDefault(m => m.Name == ToolCatalog.BackgroundStart);
                    if (started is null) call = new("parent-start", ToolCatalog.BackgroundStart,
                        """{"objective":"Complete the owned task after a bounded wait","reportCompletion":false}""");
                    else
                    {
                        using var json = JsonDocument.Parse(started.Text);
                        call = new("parent-wait", ToolCatalog.ExecutionWait, JsonSerializer.Serialize(new { mode = "background",
                            backgroundSessionIds = new[] { json.RootElement.GetProperty("backgroundSessionId").GetGuid() }, until = "all", timeoutSeconds = mode == "timeout" ? .1 : 20 }));
                    }
                }
                else call = new("parent-wait", ToolCatalog.ExecutionWait, JsonSerializer.Serialize(new { mode = "duration", seconds = mode is "cancel" or "restart" or "queued" ? 5 : 1 }));
            }
            if (call is not null)
            {
                yield return new ModelToolCallEvent(call); yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            using var result = JsonDocument.Parse(wait!.Text);
            var reason = result.RootElement.GetProperty("reason").GetString();
            yield return new ModelSemanticResponseReady(new("Wait completed: " + reason, new(ModelSpeechMode.None, null), []));
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }

    private static async Task<AgentRun> AwaitTenantWaitState(ITenantRunStore runs, Guid tenant, Guid id, bool terminal)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        while (true)
        {
            var run = (await runs.FindTenantRunAsync(tenant, id))!;
            if (terminal ? run.IsTerminal : run.Status == AgentRunStatus.WaitingForSignal) return run;
            Assert.False(!terminal && run.IsTerminal, $"Run ended before waiting: {run.Status} {run.Failure?.Code} {run.Failure?.Summary}");
            await Task.Delay(20, timeout.Token);
        }
    }

    [Theory(Timeout = 120000)] [InlineData(false, "duration")] [InlineData(true, "duration")]
    [InlineData(false, "background")] [InlineData(true, "background")]
    [InlineData(false, "timeout")] [InlineData(true, "timeout")]
    [InlineData(false, "cancel")] [InlineData(true, "cancel")]
    [InlineData(false, "handoff")] [InlineData(true, "handoff")]
    [InlineData(true, "restart")] [InlineData(false, "queued")] [InlineData(true, "queued")]
    public async Task Tenant_wait_keeps_one_Run_and_attempt_releases_worker_and_resumes_or_cancels_without_duplicate_output(bool sqlite, string mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-wait-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        WebApplicationFactory<Program>? reopened = null; HttpClient? nextOwner = null;
        try
        {
            var model = new TenantWaitModel(mode);
            await using WebApplicationFactory<Program> initial = sqlite ? new TenantBackgroundSqliteFactory(root, model) : new TenantBackgroundMemoryFactory(root, model);
            var factory = initial; using var firstOwner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") }); var owner = firstOwner;
            var setup = await StartTenantBackground(factory, owner, submit: false); var tenant = setup.Tenant; var api = $"/api/v2/tenants/{tenant}";
            var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var sessions = factory.Services.GetRequiredService<ITenantSessionStore>();
            var session = (await sessions.FindTenantSessionAsync(tenant, setup.Session))!;
            var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var access = factory.Services.GetRequiredService<TenantAccessService>();
            var role = await access.SaveRoleAsync(human, tenant, null, "Owned child result reader", ["agents.read", "sessions.read", "agent-runs.read", "workspaces.read"], await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, session.Owner.ExecutorPrincipalId, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
            if (mode == "handoff")
            {
                Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsync(api + $"/agents/{session.Owner.AgentInstanceId}/workspace/content?path=%2Fhome%2Fwait-source.txt&expectedRevision={await Revision(owner, tenant)}",
                    new ByteArrayContent("Exact child evidence"u8.ToArray()))).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/workspaces.read", new { mode = "always", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
            }
            var id = (await Json(await owner.PostAsJsonAsync(api + $"/sessions/{setup.Session}/messages", new { messageId = Guid.NewGuid(), text = "Exercise the bounded same-Run wait", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }))).GetProperty("runId").GetGuid();
            var waiting = await AwaitTenantWaitState(runs, tenant, id, false);
            Assert.Null(waiting.Claim); Assert.Equal(1, waiting.WaitCount); Assert.Equal(1, waiting.AttemptCount);
            Assert.NotNull(waiting.Wait!.SuspendedGeneration);
            if (mode == "handoff")
            {
                var target = Assert.Single(waiting.Wait.BackgroundSessionIds); var childSession = (await sessions.FindTenantSessionAsync(tenant, target))!;
                AgentRun childWaiting; using var consentTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                while (true)
                {
                    childWaiting = (await runs.FindTenantRunAsync(tenant, childSession.Snapshot.Origin.InitialBackgroundAgentRunId!.Value))!;
                    if (childWaiting.Status == AgentRunStatus.WaitingForApproval) break;
                    Assert.False(childWaiting.IsTerminal, $"Child ended before review: {childWaiting.Status} {childWaiting.Failure?.Code} {childWaiting.Result?.Text}; "
                        + string.Join(" | ", model.Requests.SelectMany(r => r.Messages).Where(m => m.Role == ModelRole.Tool).Select(m => m.Name + ":" + m.Text).Distinct().TakeLast(8)) + " Logs: " + string.Join(" | ", model.Failures));
                    await Task.Delay(20, consentTimeout.Token);
                }
                var request = (await factory.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, childWaiting.ApprovalRequestId!.Value))!;
                var detail = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}"));
                Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = "approve", actionHash = request.Operation.ActionHash,
                    expectedRevision = detail.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = request.Revision })).StatusCode);
                using var successorTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                while (!model.SuccessorEntered.Task.IsCompleted)
                {
                    var initialChild = (await runs.FindTenantRunAsync(tenant, childWaiting.AgentRunId))!;
                    var leaf = initialChild.Result?.ContinuationRunId is { } leafId ? (await runs.FindTenantRunAsync(tenant, leafId))! : initialChild;
                    Assert.False(leaf.IsTerminal && leaf.Result?.OutcomeKind != AgentRunOutcomeKind.Continued,
                        $"Child successor ended before the model barrier: {leaf.Status} {leaf.Failure?.Code} {leaf.Failure?.Summary}; "
                        + string.Join(" | ", model.Requests.SelectMany(r => r.Messages).Where(m => m.Role == ModelRole.Tool).Select(m => m.Name + ":" + m.Text).Distinct().TakeLast(8))
                        + " Logs: " + string.Join(" | ", model.Failures));
                    await Task.Delay(20, successorTimeout.Token);
                }
                var handed = (await runs.FindTenantRunAsync(tenant, childWaiting.AgentRunId))!;
                Assert.Equal(AgentRunOutcomeKind.Continued, handed.Result!.OutcomeKind);
                Assert.Equal(AgentRunStatus.WaitingForSignal, (await runs.FindTenantRunAsync(tenant, id))!.Status);
                model.ReleaseSuccessor.TrySetResult();
            }
            if (mode == "restart")
            {
                await initial.DisposeAsync(); reopened = new TenantBackgroundSqliteFactory(root, new TenantWaitModel(mode)); factory = reopened;
                nextOwner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") }); owner = nextOwner;
                await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
                runs = factory.Services.GetRequiredService<ITenantRunStore>(); sessions = factory.Services.GetRequiredService<ITenantSessionStore>();
                var retained = (await runs.FindTenantRunAsync(tenant, id))!;
                Assert.Equal(JsonSerializer.Serialize(waiting.Wait), JsonSerializer.Serialize(retained.Wait));
                Assert.Equal(JsonSerializer.Serialize(waiting.Checkpoint), JsonSerializer.Serialize(retained.Checkpoint));
            }
            Guid? queued = null;
            if (mode == "queued")
            {
                var current = (await sessions.FindTenantSessionAsync(tenant, setup.Session))!;
                queued = (await Json(await owner.PostAsJsonAsync(api + $"/sessions/{setup.Session}/messages", new { messageId = Guid.NewGuid(), text = "A separate queued human task", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = current.Snapshot.Revision }))).GetProperty("runId").GetGuid();
                Assert.Equal(AgentRunStatus.Queued, (await runs.FindTenantRunAsync(tenant, queued.Value))!.Status);
            }
            if (mode == "cancel") Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/agent-runs/{id}/cancel", new { expectedRevision = await Revision(owner, tenant), expectedRunRevision = waiting.Revision })).StatusCode);
            var done = await AwaitTenantWaitState(runs, tenant, id, true);
            Assert.Equal(mode == "cancel" ? AgentRunStatus.Cancelled : AgentRunStatus.Completed, done.Status);
            Assert.Equal(1, done.AttemptCount); Assert.Equal(1, done.WaitCount); Assert.Null(done.Wait);
            if (mode != "cancel")
            {
                var expected = mode is "background" or "handoff" ? "condition_met" : mode == "timeout" ? "timeout" : "elapsed";
                Assert.Contains(expected, done.Result!.Text); Assert.Equal(waiting.ResponseId, done.ResponseId); Assert.Equal(waiting.ActivationId, done.ActivationId);
                Assert.True(AgentCore.Application.Execution.AgentRunToolCallCheckpoint.TryRead(done.Checkpoint, out var messages));
                var result = Assert.Single(messages!, m => m.Role == ModelRole.Tool && m.ToolCallId == "parent-wait");
                Assert.Contains(expected, result.Text);
                var history = await Json(await owner.GetAsync(api + $"/sessions/{setup.Session}/messages?limit=100"));
                Assert.Single(history.GetProperty("items").EnumerateArray(), e => e.GetProperty("role").GetString() == "assistant" && e.GetProperty("text").GetString() == done.Result.Text);
            }
            if (queued is { } queuedId) Assert.Equal(AgentRunStatus.Completed, (await AwaitTenantWaitState(runs, tenant, queuedId, true)).Status);
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
            Assert.Empty(model.Failures);
        }
        finally { nextOwner?.Dispose(); if (reopened is not null) await reopened.DisposeAsync(); SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
