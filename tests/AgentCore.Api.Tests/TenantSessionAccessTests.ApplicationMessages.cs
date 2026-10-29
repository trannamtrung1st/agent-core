using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    private const string ProgressSource = "Private progress source: thử nghiệm";
    private const string ProgressNotice = "Still checking: " + ProgressSource;
    private sealed class TenantProgressModel(string mode) : ILanguageModel
    {
        private readonly ScriptedLanguageModel inner = new();
        private Guid? firstResponse;
        public ModelCapabilities Capabilities => inner.Capabilities;
        public ConcurrentQueue<ModelRequest> Requests { get; } = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource NoticesEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource NoticesRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Enqueue(request); firstResponse ??= request.ResponseId;
            if (request.ResponseId != firstResponse)
            {
                if (mode == "steer-after-notice")
                {
                    await foreach (var item in inner.GenerateAsync(request, cancellationToken)) yield return item;
                    yield break;
                }
                if (!request.Messages.Any(m => m.Role == ModelRole.Tool && m.ToolCallId == "next-source"))
                {
                    Assert.DoesNotContain(ProgressNotice, string.Join("\n", request.Messages.Select(m => m.Text)));
                    yield return new ModelToolCallEvent(new("next-source", ToolCatalog.WorkspaceRead, "{\"path\":\"/home/progress.txt\"}"));
                    yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
                }
                if (!request.Messages.Any(m => m.Role == ModelRole.Tool && m.ToolCallId == "fresh-notice"))
                {
                    Assert.Contains("12 intermediate messages remain", Assert.Single(request.Tools!, t => t.Name == ToolCatalog.AppMessageSend).Description);
                    yield return new ModelToolCallEvent(new("fresh-notice", ToolCatalog.AppMessageSend, "{\"text\":\"Fresh turn progress\"}"));
                    yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
                }
                await foreach (var item in inner.GenerateAsync(request, cancellationToken)) yield return item;
                yield break;
            }
            if (!request.Messages.Any(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.WorkspaceRead))
            {
                yield return new ModelToolCallEvent(new("progress-source", ToolCatalog.WorkspaceRead, "{\"path\":\"/home/progress.txt\"}"));
                yield return new ModelToolCallEvent(new("premature", ToolCatalog.AppMessageSend, "{\"text\":\"Premature notice\"}"));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            if (!request.Messages.Any(m => m.Role == ModelRole.Tool && m.ToolCallId == "notice-a"))
            {
                Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(30)); // Cancellation deliberately emits a late noncooperative call.
                yield return new ModelToolCallEvent(new("notice-a", ToolCatalog.AppMessageSend, JsonSerializer.Serialize(new { text = ProgressNotice })));
                if (mode == "budget")
                {
                    for (var i = 1; i <= 12; i++)
                        yield return new ModelToolCallEvent(new("budget-" + i, ToolCatalog.AppMessageSend, JsonSerializer.Serialize(new { text = "Checking step " + i })));
                }
                else
                {
                    yield return new ModelToolCallEvent(new("same-text", ToolCatalog.AppMessageSend, JsonSerializer.Serialize(new { text = ProgressNotice })));
                    yield return new ModelToolCallEvent(new("routing", ToolCatalog.AppMessageSend, JsonSerializer.Serialize(new { text = "Routed notice", sessionId = Guid.NewGuid() })));
                    yield return new ModelToolCallEvent(new("empty", ToolCatalog.AppMessageSend, "{\"text\":\"  \"}"));
                    yield return new ModelToolCallEvent(new("oversized", ToolCatalog.AppMessageSend, JsonSerializer.Serialize(new { text = new string('x', 2001) })));
                    yield return new ModelToolCallEvent(new("notice-b", ToolCatalog.AppMessageSend, "{\"text\":\"Checking the next step\"}"));
                }
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            if (mode is "cancel-after-notice" or "steer-after-notice")
            {
                NoticesEntered.TrySetResult(); await NoticesRelease.Task.WaitAsync(TimeSpan.FromSeconds(30));
                yield return new ModelToolCallEvent(new("late-notice", ToolCatalog.AppMessageSend, "{\"text\":\"Late retired notice\"}"));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            if (mode is "reused-id" or "reused-same-id")
            {
                yield return new ModelToolCallEvent(new("notice-a", ToolCatalog.AppMessageSend, JsonSerializer.Serialize(new { text = mode == "reused-same-id" ? ProgressNotice : "Changed duplicate" })));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            if (mode == "final-failed")
            {
                yield return new ModelFailed(new ProviderFailure(ProviderErrorCode.InvalidResponse, "Synthetic final failure")); yield break;
            }
            await foreach (var item in inner.GenerateAsync(request, cancellationToken)) yield return item;
        }
    }

    [Theory(Timeout = 120000)] [InlineData(false, "allowed")] [InlineData(true, "allowed")]
    [InlineData(false, "budget")] [InlineData(true, "budget")]
    [InlineData(false, "source-changed")] [InlineData(true, "source-changed")]
    [InlineData(false, "executor-revoked")] [InlineData(true, "executor-revoked")]
    [InlineData(false, "home-disabled")] [InlineData(true, "home-disabled")]
    [InlineData(false, "cancel")] [InlineData(true, "cancel")]
    [InlineData(false, "cancel-after-notice")] [InlineData(true, "cancel-after-notice")]
    [InlineData(false, "steer-after-notice")] [InlineData(true, "steer-after-notice")]
    [InlineData(false, "reused-id")] [InlineData(true, "reused-id")]
    [InlineData(false, "reused-same-id")] [InlineData(true, "reused-same-id")]
    [InlineData(false, "final-failed")] [InlineData(true, "final-failed")]
    public async Task Tenant_application_messages_use_owned_live_conversation_and_current_source_authority(bool sqlite, string mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-progress-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new TenantProgressModel(mode);
            var diagnostics = new DiagnosticLineLoggerProvider(LogLevel.Warning);
            void Configure(IServiceCollection services) => services.AddLogging(logging => logging.AddProvider(diagnostics));
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model, Configure) : new NativeTenantMemoryFactory(root, model, Configure);
            using var owner = factory.CreateClient(); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 36, expectedRevision = await Revision(owner, tenant) }));
            var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid(); var executor = agent.GetProperty("principalId").GetGuid();
            await GrantNativePrivateSpaceRead(owner, tenant, instance);
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
            var workspace = await PrivateWorkspacePath(owner, tenant, instance) + "/content?path=%2Fhome%2Fprogress.txt&expectedRevision=";
            Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsync(workspace + await Revision(owner, tenant), new ByteArrayContent(Encoding.UTF8.GetBytes(ProgressSource)))).StatusCode);
            var access = factory.Services.GetRequiredService<TenantAccessService>();
            var role = await access.SaveRoleAsync(human, tenant, null, "Progress source read", ["agents.read", "sessions.read", "workspaces.read"], await Revision(owner, tenant));
            var assignment = await access.AssignRoleAsync(human, tenant, executor, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
            var spaceId = await NativePrivateSpaceId(owner, tenant, instance);
            var text = mode == "source-changed" ? "Read @homeFile:" + spaceId.ToString("D") + "/home/progress.txt" : "Read the file, report progress, then finish";
            object[]? parts = mode == "source-changed" ? [new { kind = "text", text = "Read " }, new { kind = "reference", reference = new { kind = "homeFile", spaceId, path = "/home/progress.txt" } }] : null;
            var sent = await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text, parts, expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 });
            Assert.True(sent.StatusCode == HttpStatusCode.Accepted, await sent.Content.ReadAsStringAsync()); var id = (await Json(sent)).GetProperty("runId").GetGuid();
            await model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(25));
            var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var running = (await runs.FindTenantRunAsync(tenant, id))!;
            try
            {
                Assert.Equal(AgentRunStatus.Running, running.Status);
                Assert.DoesNotContain(model.Requests.First().Tools!, t => t.Name == ToolCatalog.AppMessageSend);
                Assert.Contains(model.Requests.Last().Tools!, t => t.Name == ToolCatalog.AppMessageSend);
                using (var read = JsonDocument.Parse(model.Requests.Last().Messages.Last(m => m.Name == ToolCatalog.WorkspaceRead && m.Role == ModelRole.Tool).Text))
                    Assert.Equal(ProgressSource, read.RootElement.GetProperty("content").GetString());
                if (mode == "source-changed") Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsync(workspace + await Revision(owner, tenant), new ByteArrayContent(Encoding.UTF8.GetBytes("Replacement source")))).StatusCode);
                if (mode == "executor-revoked") await access.RevokeAssignmentAsync(human, tenant, assignment.AssignmentId, await Revision(owner, tenant));
                if (mode == "home-disabled") Assert.Equal(HttpStatusCode.OK, (await owner.PatchAsJsonAsync(api + $"/agents/{instance}/settings/stateIsolationPolicy", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1, set = new { homeAccess = "Disabled" }, clear = Array.Empty<string>() })).StatusCode);
                if (mode == "cancel") Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/agent-runs/{id}/cancel", new { expectedRevision = await Revision(owner, tenant), expectedRunRevision = running.Revision })).StatusCode);
            }
            finally { model.Release.TrySetResult(); }
            Guid? replacement = null;
            if (mode is "cancel-after-notice" or "steer-after-notice")
            {
                try { await model.NoticesEntered.Task.WaitAsync(TimeSpan.FromSeconds(25)); }
                catch (TimeoutException)
                {
                    var requests = model.Requests.Count;
                    AgentRun? observed = null;
                    var read = "completed";
                    using var diagnosticDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try
                    {
                        observed = await runs.FindTenantRunAsync(tenant, id, diagnosticDeadline.Token).AsTask()
                            .WaitAsync(diagnosticDeadline.Token);
                    }
                    catch (OperationCanceledException) { read = "timeout"; }
                    catch { read = "unavailable"; }
                    throw new TimeoutException($"Notice continuation did not enter within 25 seconds. RunRead={read} "
                        + $"Status={observed?.Status.ToString() ?? "missing"} Revision={observed?.Revision} "
                        + $"FailureCode={observed?.Failure?.Code ?? "none"} DiagnosticId={observed?.Failure?.DiagnosticId} "
                        + $"ModelRequests={requests} CommittedSteps={observed?.Checkpoint?.StepCount}; "
                        + $"diagnostics={SafePreparationDiagnostics(diagnostics)}");
                }
                try
                {
                    var current = (await runs.FindTenantRunAsync(tenant, id))!;
                    if (mode == "cancel-after-notice")
                        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/agent-runs/{id}/cancel", new { expectedRevision = await Revision(owner, tenant), expectedRunRevision = current.Revision })).StatusCode);
                    else
                    {
                        var steered = await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Hello replacement objective", behavior = "interrupt", interruptedRunId = id,
                            expectedRunRevision = current.Revision, expectedRevision = await Revision(owner, tenant), expectedSessionRevision = (await Json(await owner.GetAsync(api + $"/sessions/{session}"))).GetProperty("revision").GetInt64() });
                        Assert.Equal(HttpStatusCode.Accepted, steered.StatusCode); replacement = (await Json(steered)).GetProperty("runId").GetGuid();
                    }
                }
                finally { model.NoticesRelease.TrySetResult(); }
            }
            var done = await AwaitBrowserRestartRun(runs, tenant, id, false);
            if (replacement is { } nextId) Assert.Equal(AgentRunStatus.Completed, (await AwaitBrowserRestartRun(runs, tenant, nextId, false)).Status);
            var history = await Json(await owner.GetAsync(api + $"/sessions/{session}/messages"));
            var notices = history.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("role").GetString() == "applicationMessage").ToArray();
            Assert.Empty((await factory.Services.GetRequiredService<ITenantApprovalStore>().ListApprovalsAsync(tenant, null, null, new(0, 100))).Items);
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
            if (mode is "allowed" or "budget" or "cancel-after-notice" or "steer-after-notice" or "reused-id" or "reused-same-id" or "final-failed")
            {
                var expected = mode is "cancel-after-notice" or "steer-after-notice" ? AgentRunStatus.Cancelled : mode is "reused-id" or "reused-same-id" or "final-failed" ? AgentRunStatus.Failed : AgentRunStatus.Completed;
                Assert.True(done.Status == expected, $"{done.Status}: {done.Failure?.Code}: {done.Failure?.Summary}");
                Assert.Equal(mode == "budget" ? 12 : 2, notices.Length);
                Assert.Equal(ProgressNotice, notices[0].GetProperty("text").GetString());
                Assert.All(notices, i => { Assert.Equal("completed", i.GetProperty("status").GetString()); Assert.Equal(0, i.GetProperty("heardTextEndExclusive").GetInt32()); });
                Assert.DoesNotContain(notices, i => i.GetProperty("text").GetString() is "Premature notice" or "Changed duplicate" or "Routed notice" or "Late retired notice");
                var observed = model.Requests.Last(r => r.ResponseId == running.ResponseId);
                if (mode == "budget")
                {
                    Assert.DoesNotContain(observed.Tools!, t => t.Name == ToolCatalog.AppMessageSend);
                    Assert.Contains("over_budget", observed.Messages.Last(m => m.Role == ModelRole.Tool && m.ToolCallId == "budget-12").Text);
                }
                else
                {
                    foreach (var call in new[] { "routing", "empty", "oversized" }) Assert.Contains("invalid", observed.Messages.Last(m => m.Role == ModelRole.Tool && m.ToolCallId == call).Text);
                    Assert.Contains("duplicate", observed.Messages.Last(m => m.Role == ModelRole.Tool && m.ToolCallId == "same-text").Text);
                }
                if (mode is "reused-id" or "reused-same-id")
                {
                    Assert.Contains(AgentRunToolCallCheckpoint.ReadExecutionState(done.Checkpoint).FailedAttempts!, failure => failure.Reason == "invalidToolCallIdentity");
                    var beforeDuplicate = observed.Messages.Where(m => m.Role == ModelRole.Tool).ToArray();
                    Assert.Equal(beforeDuplicate.Length, done.Checkpoint!.StepCount);
                    Assert.True(AgentRunToolCallCheckpoint.TryRead(done.Checkpoint, out var retained));
                    Assert.Equal(beforeDuplicate.Select(m => JsonSerializer.Serialize(m)), retained!.Where(m => m.Role == ModelRole.Tool).Select(m => JsonSerializer.Serialize(m)));
                    Assert.Single(retained!, m => m.Role == ModelRole.Tool && m.ToolCallId == "notice-a");
                }
                if (mode is "allowed" or "budget") Assert.Contains(history.GetProperty("items").EnumerateArray(), i => i.GetProperty("role").GetString() == "assistant" && i.GetProperty("text").GetString()!.StartsWith("Order 91 is delayed.", StringComparison.Ordinal));
                var assistant = Assert.Single(history.GetProperty("items").EnumerateArray(), i => i.GetProperty("role").GetString() == "assistant" && i.GetProperty("responseId").GetGuid() == running.ResponseId);
                Assert.True(assistant.GetProperty("createdAt").GetDateTimeOffset() <= notices[0].GetProperty("createdAt").GetDateTimeOffset());
                if (assistant.TryGetProperty("completedAt", out var completed) && completed.ValueKind == JsonValueKind.String)
                    Assert.True(completed.GetDateTimeOffset() >= notices[^1].GetProperty("createdAt").GetDateTimeOffset());
                if (mode is not ("allowed" or "budget")) return;
                var next = await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Next conversation turn", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = (await Json(await owner.GetAsync(api + $"/sessions/{session}"))).GetProperty("revision").GetInt64() });
                Assert.Equal(HttpStatusCode.Accepted, next.StatusCode);
                Assert.Equal(AgentRunStatus.Completed, (await AwaitBrowserRestartRun(runs, tenant, (await Json(next)).GetProperty("runId").GetGuid(), false)).Status);
                Assert.DoesNotContain(ProgressNotice, string.Join("\n", model.Requests.Last().Messages.Select(m => m.Text)));
                if (sqlite)
                {
                    await factory.DisposeAsync();
                    await using var reopened = new NativeTenantSqliteFactory(root, new ScriptedLanguageModel());
                    using var reader = reopened.CreateClient(); await Login(reader, "owner@example.test", "explicit-synthetic-test-password");
                    var restored = await Json(await reader.GetAsync(api + $"/sessions/{session}/messages"));
                    Assert.Equal(notices.Length + 1, restored.GetProperty("items").EnumerateArray().Count(i => i.GetProperty("role").GetString() == "applicationMessage"));
                }
            }
            else
            {
                Assert.Empty(notices); Assert.DoesNotContain(ProgressNotice, history.GetRawText());
                Assert.True(done.IsTerminal); if (mode == "cancel") Assert.Equal(AgentRunStatus.Cancelled, done.Status);
            }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
