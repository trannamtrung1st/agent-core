using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    private sealed class LiveClaimIamModel : ILanguageModel
    {
        private readonly ScriptedLanguageModel inner = new();
        public ModelCapabilities Capabilities => inner.Capabilities;
        public Guid Instance;
        public ConcurrentQueue<ModelRequest> Requests { get; } = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Enqueue(request);
            if (Requests.Count == 1) { Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
            var tool = !request.Messages.Any(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.IamCheckAccess) ? ToolCatalog.IamCheckAccess
                : !request.Messages.Any(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.IamEffectivePermissions) ? ToolCatalog.IamEffectivePermissions : null;
            if (tool is not null)
            {
                Assert.Contains(request.Tools!, t => t.Name == tool);
                yield return new ModelToolCallEvent(new("live-" + tool, tool, tool == ToolCatalog.IamCheckAccess
                    ? JsonSerializer.Serialize(new { permission = "agents.read", resourceType = "agent", resourceId = Instance })
                    : JsonSerializer.Serialize(new { resourceType = "agent", resourceId = Instance })));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            await foreach (var item in inner.GenerateAsync(request, cancellationToken)) yield return item;
        }
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Actual_tenant_model_IAM_tools_keep_current_claims_and_refuse_stale_parallel_frames(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-tool-live-lease-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new LiveClaimIamModel();
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model) : new NativeTenantMemoryFactory(root, model);
            using var owner = factory.CreateClient(); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) }));
            model.Instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid();
            await GrantNativePrivateSpaceRead(owner, tenant, model.Instance);
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = model.Instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
            var response = await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Inspect your own current access", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode); var id = (await Json(response)).GetProperty("runId").GetGuid();
            await model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20)); var runs = factory.Services.GetRequiredService<ITenantRunStore>();
            var active = (await runs.FindTenantRunAsync(tenant, id))!; Assert.Equal(AgentRunStatus.Running, active.Status);
            var stale = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId: model.Instance, AgentRunId: id,
                OwnedSessionId: session, TenantId: tenant, AgentRunGeneration: Guid.NewGuid());
            var executor = factory.Services.GetRequiredService<SessionToolExecutor>();
            foreach (var name in new[] { ToolCatalog.IamCheckAccess, ToolCatalog.IamEffectivePermissions, ToolCatalog.AutomationCreate, ToolCatalog.AutomationList,
                ToolCatalog.AttachmentsRead, ToolCatalog.SkillsLoad, ToolCatalog.RequestAuthorization, ToolCatalog.ExecutionWait })
            {
                var call = new ModelToolCall("stale-model", name, "{invalid}");
                Assert.Equal(ToolPolicyDecision.Deny, await executor.EvaluateExecutionPolicyAsync(active.Admission.Configuration!.Definition, session, call, default, stale, default));
                using var denied = JsonDocument.Parse((await executor.ExecuteAsync(active.Admission.Configuration.Definition, session, call, 4096, admission: stale)).Text);
                Assert.Equal("forbidden", denied.RootElement.GetProperty("error").GetString());
            }
            Assert.Single(model.Requests); model.Release.TrySetResult();
            var done = await AwaitBrowserRestartRun(runs, tenant, id, false);
            Assert.True(done.Status == AgentRunStatus.Completed, $"{done.Status}: {done.Failure?.Code}: {done.Failure?.Summary}");
            foreach (var name in new[] { ToolCatalog.IamCheckAccess, ToolCatalog.IamEffectivePermissions })
            {
                var value = model.Requests.Last().Messages.Last(m => m.Role == ModelRole.Tool && m.Name == name);
                using var result = JsonDocument.Parse(value.Text); Assert.False(result.RootElement.TryGetProperty("error", out _));
                Assert.True(result.RootElement.TryGetProperty(name == ToolCatalog.IamCheckAccess ? "decision" : "operations", out _));
            }
            Assert.Empty((await factory.Services.GetRequiredService<ITenantApprovalStore>().ListApprovalsAsync(tenant, null, null, new(0, 100))).Items);
            var history = await Json(await owner.GetAsync(api + $"/sessions/{session}/messages"));
            Assert.Contains(history.GetProperty("items").EnumerateArray(), i => i.GetProperty("role").GetString() == "assistant"
                && i.GetProperty("text").GetString()!.StartsWith("Order 91 is delayed.", StringComparison.Ordinal));
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
