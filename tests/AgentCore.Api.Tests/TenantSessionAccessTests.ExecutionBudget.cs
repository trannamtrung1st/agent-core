using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Context;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Browser;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory]
    [InlineData(false, "ten")] [InlineData(true, "ten")]
    [InlineData(false, "interactive")] [InlineData(true, "interactive")]
    [InlineData(false, "steps")] [InlineData(true, "steps")]
    [InlineData(false, "capacity")] [InlineData(true, "capacity")]
    [InlineData(false, "update")] [InlineData(true, "update")]
    public async Task Tenant_execution_budget_keeps_real_workspace_effects_bounded_and_admission_pins_immutable(bool sqlite, string mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-execution-budget-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new OwnedBudgetModel(mode);
            void Configure(IServiceCollection services)
            {
                if (mode != "interactive") return;
                var options = (BrowserOptions)services.Single(d => d.ServiceType == typeof(BrowserOptions)).ImplementationInstance!;
                options.Enabled = true; options.Headless = true; options.Channel = null; options.FixtureEnabled = true; options.FixturePort = 0;
                options.ProfileRoot = Path.Combine(root, "browser"); options.PolicyMode = "Restricted"; options.ProfileMode = "EphemeralSession";
            }
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model, Configure) : new NativeTenantMemoryFactory(root, model, Configure);
            using var owner = factory.CreateClient(); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 35, expectedRevision = await Revision(owner, tenant) }));
            var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid(); var executor = agent.GetProperty("principalId").GetGuid();
            var instanceRevision = 1L;
            if (mode != "interactive")
            {
                Assert.Equal(HttpStatusCode.OK, (await owner.PatchAsJsonAsync(api + $"/agents/{instance}/settings/capabilities", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = instanceRevision++,
                    set = new { selected = new[] { ToolCatalog.WorkspaceWrite }, always = new[] { ToolCatalog.WorkspaceWrite } }, clear = Array.Empty<string>() })).StatusCode);
            }
            if (mode == "capacity")
            {
                var changed = await Json(await owner.PutAsJsonAsync(api + $"/agents/{instance}/execution-budgets", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = instanceRevision,
                    budgets = new ExecutionBudgetPolicy(Standard: new(144, 900)) }));
                instanceRevision = changed.GetProperty("instance").GetProperty("revision").GetInt64();
            }
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = instanceRevision }))).GetProperty("sessionId").GetGuid();
            var access = factory.Services.GetRequiredService<TenantAccessService>();
            var operations = mode == "interactive" ? new[] { "agents.read", "sessions.read", "workspaces.read", "workspaces.write", "browser.read", "browser.use" }
                : ["agents.read", "sessions.read", "workspaces.read", "workspaces.write"];
            var role = await access.SaveRoleAsync(human, tenant, null, "Workspace budget execution", operations, await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, executor, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
            async Task<Guid> Submit(string text, long revision) => (await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text, expectedRevision = await Revision(owner, tenant), expectedSessionRevision = revision }))).GetProperty("runId").GetGuid();
            var id = await Submit("Write each requested part, then report only the saved parts.", 1);
            var runs = factory.Services.GetRequiredService<ITenantRunStore>();
            if (mode == "update")
            {
                await model.FirstWriteObserved.Task.WaitAsync(TimeSpan.FromSeconds(20));
                try
                {
                    var pinned = (await runs.FindTenantRunAsync(tenant, id))!;
                    Assert.Equal(24, pinned.Admission.ExecutionBudget!.Profile.MaxSteps);
                    Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + $"/agents/{instance}/execution-budgets", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = instanceRevision,
                        budgets = new ExecutionBudgetPolicy(Standard: new(40, 240)) })).StatusCode);
                }
                finally { model.Release.TrySetResult(); }
            }
            var done = await AwaitBrowserRestartRun(runs, tenant, id, false);
            Assert.True(done.Status == AgentRunStatus.Completed, $"{done.Status}: {done.Failure?.Code}: {done.Failure?.Summary}");
            Assert.True(AgentRunToolCallCheckpoint.TryRead(done.Checkpoint, out var messages));
            var writeResults = messages!.Where(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.WorkspaceWrite).ToArray();
            var writes = writeResults.Where(m => !m.Text.Contains("\"error\"", StringComparison.Ordinal)).ToArray();
            Assert.NotEmpty(writes);
            Assert.All(writeResults.Except(writes), m => Assert.Contains("finish_required", m.Text));
            Assert.All(writes, m => { using var doc = JsonDocument.Parse(m.Text); Assert.True(doc.RootElement.GetProperty("applied").GetBoolean()); Assert.False(doc.RootElement.GetProperty("replayAllowed").GetBoolean()); });
            Assert.Empty(AgentRunToolCallCheckpoint.PendingCalls(messages!));
            var expected = mode switch { "ten" => 10, "interactive" => 25, "steps" => 24, "update" => 1, _ => writes.Length };
            Assert.Equal(expected, writes.Length); Assert.InRange(done.Checkpoint!.StepCount - writes.Length, 0, 1);
            Assert.Equal(mode == "interactive" ? 48 : mode == "capacity" ? 144 : 24, done.Admission.ExecutionBudget!.Profile.MaxSteps);
            var reason = RunBudgetDiagnosticProjection.From(done)!.TerminationReason;
            if (mode is "steps" or "capacity")
            {
                Assert.Equal(mode == "steps" ? "stepLimit" : "checkpointCapacity", reason);
                Assert.DoesNotContain(model.Requests.Last().Tools ?? [], t => t.Name == ToolCatalog.WorkspaceWrite);
                if (mode == "capacity") Assert.InRange(writes.Length, 20, 49);
            }
            else Assert.Null(reason);
            var workspace = api + $"/agents/{instance}/workspace/content?path=";
            for (var index = 0; index < writes.Length; index++)
                Assert.Equal(model.Content(index), Encoding.UTF8.GetString(await owner.GetByteArrayAsync(workspace + Uri.EscapeDataString("/home/part-" + index + ".txt"))));
            Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync(workspace + Uri.EscapeDataString("/home/part-" + writes.Length + ".txt"))).StatusCode);
            var audit = await factory.Services.GetRequiredService<IIamStore>().ReadAuditAsync(tenant, 200);
            Assert.Equal(expected, audit.Count(a => a.Action == TenantWorkspaceEffectRules.Dispatch));
            Assert.Equal(expected, audit.Count(a => a.Action == TenantWorkspaceEffectRules.Result));
            if (mode == "update")
            {
                var current = await factory.Services.GetRequiredService<ITenantConversationStore>().ReadTenantConversationAsync(tenant, session, null, null, 100);
                var next = await Submit("Write the next requested part.", current!.SessionRevision);
                var second = await AwaitBrowserRestartRun(runs, tenant, next, false);
                Assert.Equal(AgentRunStatus.Completed, second.Status);
                Assert.Equal(40, second.Admission.ExecutionBudget!.Profile.MaxSteps); Assert.Equal("instance", second.Admission.ExecutionBudget.Source);
                Assert.Equal(24, (await runs.FindTenantRunAsync(tenant, id))!.Admission.ExecutionBudget!.Profile.MaxSteps);
                Assert.Equal(1, second.Checkpoint!.StepCount);
            }
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class OwnedBudgetModel(string mode) : ILanguageModel
    {
        private bool held;
        public ConcurrentQueue<ModelRequest> Requests { get; } = new();
        public TaskCompletionSource FirstWriteObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ModelCapabilities Capabilities => new(true, true, Tools: true, StructuredOutput: true);
        public string Content(int index) => (mode == "capacity" ? new string('x', 7000) : "Verified tenant part ") + index;
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Enqueue(request);
            var count = request.Messages.Count(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.WorkspaceWrite);
            if (mode == "update" && count == 1 && !held)
            {
                held = true; FirstWriteObserved.TrySetResult(); await Release.Task.WaitAsync(ct);
            }
            var target = mode switch { "ten" => 10, "interactive" => 25, "update" => 1, _ => 50 };
            if (count < target && request.Tools?.Any(t => t.Name == ToolCatalog.WorkspaceWrite) == true)
            {
                yield return new ModelToolCallEvent(new("budget-part-" + count, ToolCatalog.WorkspaceWrite, JsonSerializer.Serialize(new { path = "/home/part-" + count + ".txt", content = Content(count) })));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            yield return new ModelSemanticResponseReady(new("Saved parts are verified; no further work was attempted.", new(ModelSpeechMode.Same, null), []));
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }
}
