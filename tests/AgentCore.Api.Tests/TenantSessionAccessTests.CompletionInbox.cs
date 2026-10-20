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
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    private sealed class TenantCompletionModel(bool handoff = false, bool readReview = false) : TenantBackgroundModel
    {
        public TaskCompletionSource ReadyForRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Acknowledged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Enqueue(request); await Task.Yield(); cancellationToken.ThrowIfCancellationRequested();
            var results = request.Messages.Where(m => m.Role == ModelRole.Tool).ToArray(); ModelToolCall call;
            if (request.Tools!.Any(t => t.Name == ToolCatalog.WorkComplete))
            {
                if (handoff && !results.Any(m => m.Name == ToolCatalog.RequestAuthorization))
                    call = new("child-read", ToolCatalog.RequestAuthorization, """{"operation":"inspect_workspace","read":{"tool":"workspace.read","arguments":{"path":"/home/inbox-evidence.txt"}}}""");
                else
                {
                    if (handoff) Assert.Contains("\"readAuthorized\":true", results.Last(m => m.Name == ToolCatalog.RequestAuthorization).Text);
                    call = new("child-finish", ToolCatalog.WorkComplete, """{"outcome":"Response","summary":"Owned child evidence","attentionRequired":false}""");
                }
            }
            else if (!results.Any(m => m.Name == ToolCatalog.BackgroundStart))
                call = new("start", ToolCatalog.BackgroundStart, """{"objective":"Gather the owned evidence","reportCompletion":true}""");
            else
            {
                using var started = JsonDocument.Parse(results.Single(m => m.Name == ToolCatalog.BackgroundStart).Text);
                var session = started.RootElement.GetProperty("backgroundSessionId").GetGuid();
                if (!results.Any(m => m.Name == ToolCatalog.ExecutionWait))
                    call = readReview ? new("wait", ToolCatalog.ExecutionWait, """{"mode":"duration","seconds":0.1}""")
                        : new("wait", ToolCatalog.ExecutionWait, JsonSerializer.Serialize(new { mode = "background", backgroundSessionIds = new[] { session }, until = "all", timeoutSeconds = 20 }));
                else if (readReview && !results.Any(m => m.Name == ToolCatalog.RequestAuthorization))
                {
                    await ReadyForRead.Task.WaitAsync(cancellationToken);
                    call = new("read-completion", ToolCatalog.RequestAuthorization, JsonSerializer.Serialize(new { operation = "inspect_background_completion", read = new { backgroundSessionId = session } }));
                }
                else if (!results.Any(m => m.Name == ToolCatalog.BackgroundList))
                {
                    if (readReview)
                    {
                        using var approved = JsonDocument.Parse(results.Last(m => m.Name == ToolCatalog.RequestAuthorization).Text);
                        Assert.True(approved.RootElement.GetProperty("readAuthorized").GetBoolean());
                        Assert.False(approved.RootElement.GetProperty("applied").GetBoolean());
                        Assert.Equal("Owned child evidence", approved.RootElement.GetProperty("completion").GetProperty("summary").GetString());
                    }
                    call = new("list", ToolCatalog.BackgroundList, """{"scope":"pending_results","limit":1}""");
                }
                else if (!results.Any(m => m.Name == ToolCatalog.BackgroundInspect))
                {
                    using var list = JsonDocument.Parse(results.Single(m => m.Name == ToolCatalog.BackgroundList).Text);
                    Assert.Equal(1, list.RootElement.GetProperty("pendingCount").GetInt32());
                    Assert.Single(list.RootElement.GetProperty("completions").EnumerateArray());
                    call = new("inspect", ToolCatalog.BackgroundInspect, JsonSerializer.Serialize(new { backgroundSessionId = session }));
                }
                else if (!results.Any(m => m.Name == ToolCatalog.BackgroundTake))
                {
                    Assert.DoesNotContain(request.Tools!, t => t.Name == ToolCatalog.BackgroundAcknowledge);
                    using var inspect = JsonDocument.Parse(results.Single(m => m.Name == ToolCatalog.BackgroundInspect).Text);
                    Assert.Equal("Owned child evidence", inspect.RootElement.GetProperty("summary").GetString());
                    call = new("take", ToolCatalog.BackgroundTake, JsonSerializer.Serialize(new { backgroundSessionId = session, revision = inspect.RootElement.GetProperty("inbox").GetProperty("revision").GetInt64() }));
                }
                else if (!results.Any(m => m.Name == ToolCatalog.BackgroundAcknowledge))
                {
                    Assert.Contains(request.Tools!, t => t.Name == ToolCatalog.BackgroundAcknowledge);
                    using var take = JsonDocument.Parse(results.Single(m => m.Name == ToolCatalog.BackgroundTake).Text);
                    call = new("ack", ToolCatalog.BackgroundAcknowledge, JsonSerializer.Serialize(new { backgroundSessionId = session, revision = take.RootElement.GetProperty("inbox").GetProperty("revision").GetInt64(),
                        token = take.RootElement.GetProperty("token").GetGuid(), usage = "Use the child evidence in this answer" }));
                }
                else
                {
                    using var ack = JsonDocument.Parse(results.Single(m => m.Name == ToolCatalog.BackgroundAcknowledge).Text);
                    Assert.True(ack.RootElement.GetProperty("acknowledgmentPending").GetBoolean());
                    Assert.Equal("Claimed", ack.RootElement.GetProperty("inbox").GetProperty("status").GetString());
                    Acknowledged.TrySetResult(); await Finish.Task.WaitAsync(cancellationToken);
                    yield return new ModelSemanticResponseReady(new("Answer incorporates the owned child evidence.", new(ModelSpeechMode.None, null), []));
                    yield return new ModelCompleted(ModelStopReason.Completed); yield break;
                }
            }
            Assert.Contains(request.Tools!, t => t.Name == call.Name);
            yield return new ModelToolCallEvent(call); yield return new ModelCompleted(ModelStopReason.ToolCalls);
        }
    }

    [Theory(Timeout = 120000)] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_completion_inbox_actual_model_reads_takes_and_acknowledges_only_with_durable_parent_answer(bool sqlite, bool handoff)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-inbox-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new TenantCompletionModel(handoff);
            await using WebApplicationFactory<Program> factory = sqlite ? new TenantBackgroundSqliteFactory(root, model) : new TenantBackgroundMemoryFactory(root, model);
            using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
            var setup = await StartTenantBackground(factory, owner, submit: false, fileCapabilities: [ToolCatalog.BackgroundStart, ToolCatalog.ExecutionWait,
                ToolCatalog.BackgroundList, ToolCatalog.BackgroundInspect, ToolCatalog.BackgroundTake, ToolCatalog.BackgroundAcknowledge, ToolCatalog.WorkspaceRead]);
            var tenant = setup.Tenant; var api = $"/api/v2/tenants/{tenant}"; var sessions = factory.Services.GetRequiredService<ITenantSessionStore>();
            var parentSession = (await sessions.FindTenantSessionAsync(tenant, setup.Session))!;
            var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var access = factory.Services.GetRequiredService<TenantAccessService>();
            var role = await access.SaveRoleAsync(human, tenant, null, "Owned completion reader", ["sessions.read", "agent-runs.read", "agents.read", "workspaces.read"], await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, parentSession.Owner.ExecutorPrincipalId, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
            if (handoff)
            {
                Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsync(api + $"/agents/{parentSession.Owner.AgentInstanceId}/workspace/content?path=%2Fhome%2Finbox-evidence.txt&expectedRevision={await Revision(owner, tenant)}", new ByteArrayContent("Owned source"u8.ToArray()))).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/workspaces.read", new { mode = "always", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
            }
            var runId = (await Json(await owner.PostAsJsonAsync(api + $"/sessions/{setup.Session}/messages", new { messageId = Guid.NewGuid(), text = "Use the child result in the answer", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }))).GetProperty("runId").GetGuid();
            var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var runtime = factory.Services.GetRequiredService<ITenantRuntimeStore>();
            if (handoff)
            {
                using var consentTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)); AgentRun waiting;
                while (true)
                {
                    var children = (await sessions.ListTenantSessionsAsync(tenant, null, 0, 100)).Items.Where(s => s.Snapshot.Origin.OriginatingSessionId == setup.Session).ToArray();
                    var candidateChild = children.Length == 1 ? await runs.FindTenantRunAsync(tenant, children[0].Snapshot.Origin.InitialBackgroundAgentRunId!.Value) : null;
                    if (candidateChild?.Status == AgentRunStatus.WaitingForApproval) { waiting = candidateChild; break; }
                    Assert.False(candidateChild?.IsTerminal == true, $"Child ended before read review: {candidateChild?.Status} {candidateChild?.Failure?.Code}");
                    await Task.Delay(20, consentTimeout.Token);
                }
                var request = (await factory.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, waiting.ApprovalRequestId!.Value))!;
                var detail = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}"));
                Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = "approve", actionHash = request.Operation.ActionHash,
                    expectedRevision = detail.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = request.Revision })).StatusCode);
            }
            string Diagnostics(AgentRun run) => $"{run.Status} {run.Failure?.Code} {run.Failure?.Summary}; "
                + string.Join(" | ", model.Requests.SelectMany(r => r.Messages).Where(m => m.Role == ModelRole.Tool).Select(m => m.Name + ":" + m.Text).Distinct().TakeLast(8))
                + " Logs: " + string.Join(" | ", model.Failures);
            using var barrierTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            while (!model.Acknowledged.Task.IsCompleted)
            {
                var observed = (await runs.FindTenantRunAsync(tenant, runId))!;
                Assert.False(observed.IsTerminal || barrierTimeout.IsCancellationRequested, Diagnostics(observed));
                await Task.Delay(20);
            }
            var parent = (await runs.FindTenantRunAsync(tenant, runId))!;
            var child = Assert.Single((await sessions.ListTenantSessionsAsync(tenant, null, 0, 100)).Items, s => s.Snapshot.Origin.OriginatingSessionId == setup.Session);
            var initialChild = (await runs.FindTenantRunAsync(tenant, child.Snapshot.Origin.InitialBackgroundAgentRunId!.Value))!;
            var leaf = initialChild;
            while (leaf.Result?.ContinuationRunId is { } nextId) leaf = (await runs.FindTenantRunAsync(tenant, nextId))!;
            Assert.Equal(handoff, initialChild.Result?.OutcomeKind == AgentRunOutcomeKind.Continued);
            var source = (await runtime.ReadTenantBackgroundCompletionAsync(parent.Owner, leaf.AgentRunId))!;
            Assert.Equal(initialChild.AgentRunId, source.InitialChild!.AgentRunId);
            Assert.Equal(CompletionInboxStatus.Claimed, source.Item!.Status); Assert.NotNull(source.Item.Acknowledgment); Assert.Null(source.Item.HandledByRunId);
            model.Finish.TrySetResult(); parent = await AwaitTenantWaitState(runs, tenant, runId, true);
            Assert.Equal(AgentRunStatus.Completed, parent.Status); Assert.NotNull(parent.Result!.OutcomeEntryId);
            source = (await runtime.ReadTenantBackgroundCompletionAsync(parent.Owner, source.Child.AgentRunId))!;
            Assert.Equal(CompletionInboxStatus.Handled, source.Item!.Status); Assert.Equal(runId, source.Item.HandledByRunId);
            Assert.Null(source.Item.ReportActivationId); Assert.Null(source.Item.ClaimToken);
            var messages = await Json(await owner.GetAsync(api + $"/sessions/{setup.Session}/messages?limit=100"));
            Assert.Single(messages.GetProperty("items").EnumerateArray(), entry => entry.TryGetProperty("text", out var text) && text.GetString() == "Answer incorporates the owned child evidence.");
            Assert.Empty(await runtime.ListTenantBackgroundCompletionsAsync(100));
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
