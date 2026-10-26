using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Context;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    private sealed class CoreProcedureModel(string key) : ILanguageModel
    {
        private int calls;
        public Guid Instance;
        public ConcurrentQueue<ModelRequest> Requests { get; } = new();
        public Func<int, CancellationToken, Task>? Before;
        public ModelCapabilities Capabilities { get; } = new AgentCore.Infrastructure.Providers.Synthetic.ScriptedLanguageModel().Capabilities;
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Enqueue(request);
            var step = Interlocked.Increment(ref calls);
            if (Before is { } callback) await callback(step, ct);
            if (step <= 2)
            {
                yield return new ModelToolCallEvent(step == 1
                    ? new("core-procedure", ToolCatalog.SkillsLoad, JsonSerializer.Serialize(new { ids = new[] { key } }))
                    : new("core-read-authority", ToolCatalog.IamCheckAccess, JsonSerializer.Serialize(new { permission = "agents.read", resourceType = "agent", resourceId = Instance.ToString("D") })));
                yield return new ModelCompleted(ModelStopReason.ToolCalls);
            }
            else
            {
                yield return new ModelTextDelta("Core procedure check completed.");
                yield return new ModelCompleted(ModelStopReason.Completed);
            }
        }
    }

    [Theory]
    [InlineData(false, "none")] [InlineData(true, "none")]
    [InlineData(false, "freeze")] [InlineData(true, "freeze")]
    [InlineData(false, "freeze_before_load")] [InlineData(true, "freeze_before_load")]
    [InlineData(false, "disable_builtin")] [InlineData(true, "disable_builtin")]
    [InlineData(false, "remove_scope")] [InlineData(true, "remove_scope")]
    [InlineData(false, "assisted")] [InlineData(true, "assisted")]
    [InlineData(false, "broaden")] [InlineData(true, "broaden")]
    [InlineData(false, "private_key")] [InlineData(true, "private_key")]
    public async Task Tenant_Core_procedures_use_claimed_policy_and_refresh_loaded_delegation_without_legacy_tools(bool sqlite, string transition)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-management-context-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var key = transition == "private_key" ? "instance:private" : ManagementBuiltInSkills.HarnessKey;
            var model = new CoreProcedureModel(key);
            await using WebApplicationFactory<Program> factory = sqlite ? new TenantApprovalSqliteFactory(root, model) : new ApprovalExecutionFactory();
            await using var host = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.Replace(ServiceDescriptor.Singleton<ILanguageModel>(model))));
            using var human = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            await Login(human, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await human.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var created = await Json(await human.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 32, expectedRevision = await Revision(human, tenant) }));
            var instance = created.GetProperty("instance").GetProperty("instanceId").GetGuid(); model.Instance = instance;
            async Task ChangePolicy(string mode, string[] scopes, bool harness = true)
            {
                var current = (await host.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!;
                var response = await human.PutAsJsonAsync(api + $"/agents/{instance}/self-management", new {
                    expectedRevision = await Revision(human, tenant), expectedInstanceRevision = current.Instance.Revision,
                    overrides = new { mode = new { value = mode }, scopes = new { value = scopes },
                        autoApply = new { value = new[] { "ConversationPreferences" } }, harnessAuthoring = new { value = harness } }
                });
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
            await ChangePolicy("Managed", transition == "broaden" ? ["KnowledgeResources"] : ["Settings", "KnowledgeResources"]);
            var agents = host.Services.GetRequiredService<ITenantAgentStore>(); var actual = (await agents.FindTenantAgentAsync(tenant, instance))!;
            var iam = host.Services.GetRequiredService<IIamStore>(); var state = (await iam.ReadTenantAsync(tenant))!; var actor = Assert.Single(state.Users).PrincipalId;
            var access = host.Services.GetRequiredService<TenantAccessService>();
            var role = await access.SaveRoleAsync(actor, tenant, null, "Core procedure execution without private Skill reads", ["agents.read"], await Revision(human, tenant));
            await access.AssignRoleAsync(actor, tenant, actual.PrincipalId, role.RoleId, AccessScope.ForResource(tenant, "agent", instance.ToString("D")), await Revision(human, tenant));
            var runIdentity = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
            var runs = host.Services.GetRequiredService<ITenantRunStore>();
            model.Before = async (step, ct) => {
                if (step == 1)
                {
                    var run = (await runs.FindTenantRunAsync(tenant, await runIdentity.Task.WaitAsync(ct), ct))!;
                    var context = await host.Services.GetRequiredService<TenantRunExecutionAuthority>().CurrentHarnessContextAsync(run, ct);
                    Assert.NotNull(context); Assert.Equal(2, context.PolicyRevision); Assert.Equal(32, context.ActiveVersion);
                    Assert.True(context.Policy.Allows(HarnessManagementScope.KnowledgeResources));
                }
                if (transition == "freeze_before_load" ? step != 1 : step != 2) return;
                if (transition is "freeze" or "freeze_before_load")
                {
                    var current = (await agents.FindTenantAgentAsync(tenant, instance, ct))!;
                    Assert.Equal(HttpStatusCode.OK, (await human.PostAsJsonAsync(api + $"/agents/{instance}/self-management/freeze", new {
                        expectedRevision = await Revision(human, tenant), expectedInstanceRevision = current.Instance.Revision
                    }, ct)).StatusCode);
                }
                else if (transition == "disable_builtin") await ChangePolicy("Managed", ["Settings", "KnowledgeResources"], false);
                else if (transition == "remove_scope") await ChangePolicy("Managed", ["Settings"]);
                else if (transition == "assisted") await ChangePolicy("Assisted", ["Settings", "KnowledgeResources"]);
                else if (transition == "broaden") await ChangePolicy("Managed", ["Settings", "KnowledgeResources"]);
            };
            var session = await Json(await human.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(human, tenant), expectedInstanceRevision = 2 }));
            var sessionId = session.GetProperty("sessionId").GetGuid();
            var response = await human.PostAsJsonAsync(api + $"/sessions/{sessionId}/messages", new { messageId = Guid.NewGuid(), text = "Inspect the delegated Core procedure without changing configuration.", expectedRevision = await Revision(human, tenant), expectedSessionRevision = 1 });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode); var runId = (await Json(response)).GetProperty("runId").GetGuid(); runIdentity.SetResult(runId);
            if (factory is ApprovalExecutionFactory memory) Volatile.Write(ref memory.Enabled, true);
            AgentRun? final = null;
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25)))
                while (!deadline.IsCancellationRequested)
                {
                    final = await runs.FindTenantRunAsync(tenant, runId, deadline.Token);
                    if (final?.IsTerminal == true) break;
                    await Task.Delay(25, deadline.Token);
                }
            Assert.NotNull(final); Assert.True(final.Status == AgentRunStatus.Completed, $"{final.Status}: {final.Failure?.Code}: {final.Failure?.Summary}");
            var requests = model.Requests.ToArray(); Assert.Equal(3, requests.Length);
            var schema = Assert.Single(requests[0].Tools!, t => t.Name == ToolCatalog.SkillsLoad).ParametersJson;
            Assert.Contains(ManagementBuiltInSkills.HarnessKey, schema); Assert.DoesNotContain("instance:private", schema);
            var loaded = transition is not ("freeze_before_load" or "private_key");
            Assert.Equal(loaded ? 1 : 0, final.SkillLoadCount);
            Assert.Equal(loaded, final.ActiveSkillKeys.Contains(ManagementBuiltInSkills.HarnessKey));
            Assert.Equal(loaded, requests[1].Context!.Text(ContextSectionId.CoreHarnessAuthoring).Length > 0);
            Assert.Equal(loaded && transition is not ("freeze" or "disable_builtin" or "remove_scope"), requests[2].Context!.Text(ContextSectionId.CoreHarnessAuthoring).Length > 0);
            var lastTools = requests[2].Tools!;
            Assert.Equal(transition is not ("freeze" or "freeze_before_load" or "assisted" or "broaden"), lastTools.Any(t => t.Name == TenantPreferenceToolContract.Name));
            Assert.DoesNotContain(lastTools, t => t.Name.StartsWith("harness.") || t.Name is "configuration.inspect" or "management.policy.update");
            if (transition is "disable_builtin" or "remove_scope")
                Assert.DoesNotContain(ManagementBuiltInSkills.HarnessKey, Assert.Single(lastTools, t => t.Name == ToolCatalog.SkillsLoad).ParametersJson);
            Assert.Empty((await host.Services.GetRequiredService<ITenantApprovalStore>().ListApprovalsAsync(tenant, null, null, new(0, 100))).Items);
            Assert.Equal(2, final.Admission.Configuration!.InstanceRevision);
            Assert.Equal(HarnessManagementMode.Managed, final.Admission.Configuration.Definition.SelfManagement!.Mode);
            Assert.False(final.Admission.Configuration.Definition.SelfManagement.Frozen);
            var history = await human.GetAsync(api + $"/sessions/{sessionId}/messages?limit=100"); Assert.Equal(HttpStatusCode.OK, history.StatusCode);
            Assert.Contains("Core procedure check completed.", await history.Content.ReadAsStringAsync());
            await Assert.ThrowsAsync<AccessDeniedException>(() => host.Services.GetRequiredService<TenantRunExecutionAuthority>().CurrentHarnessContextAsync(final).AsTask());
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
