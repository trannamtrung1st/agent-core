using System.Collections.Concurrent;
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
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;
public sealed partial class TenantSessionAccessTests
{
    private sealed class ManagementReadModel(string tool, string? section = null) : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new AgentCore.Infrastructure.Providers.Synthetic.ScriptedLanguageModel().Capabilities;
        public bool Preferences; public bool ForceConsent; public string? ArgumentsOverride;
        public ConcurrentQueue<ModelRequest> Requests { get; } = new();
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); Requests.Enqueue(request);
            var arguments = ArgumentsOverride ?? (section is null ? "{}" : JsonSerializer.Serialize(new { section }));
            var messages = request.Messages.Where(m => m.Role == ModelRole.Tool).ToArray();
            var lastRead = messages.LastOrDefault(m => m.Name == tool);
            ModelToolCall? call = null;
            if (!messages.Any(m => m.Name == ToolCatalog.CapabilitiesLoad) && request.Tools?.Any(t => t.Name == ToolCatalog.CapabilitiesLoad) == true)
                call = new("discover-management", ToolCatalog.CapabilitiesLoad, JsonSerializer.Serialize(new { query = tool, limit = 1 }));
            else if (lastRead is null && request.Tools?.Any(t => t.Name == tool) == true) call = new("observe-management", tool, arguments);
            else if (lastRead is not null && !messages.Any(m => m.Name == ToolCatalog.RequestAuthorization))
            {
                using var body = JsonDocument.Parse(lastRead.Text);
                if (ForceConsent || body.RootElement.TryGetProperty("decision", out var decision) && decision.GetString() == "requiresApproval")
                    call = new("consent-management", ToolCatalog.RequestAuthorization, JsonSerializer.Serialize(new { operation = "inspect_management", read = new { tool, arguments = JsonSerializer.Deserialize<JsonElement>(arguments) } }));
            }
            var observed = messages.Any(m => m.Text.Contains("\"tenantManagementRead\":true", StringComparison.Ordinal));
            if (call is null && observed && Preferences && !messages.Any(m => m.Name == TenantPreferenceToolContract.Name))
                call = new("management-preferences", TenantPreferenceToolContract.Name, """{"set":{"language":"vi"},"clear":[]}""");
            if (call is not null) { yield return new ModelToolCallEvent(call); yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break; }
            yield return new ModelTextDelta(observed ? "Observed the exact management snapshot; no configuration authority is implied." : "Management read was not authorized; no values were read through this operation.");
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }
    private static async Task<(Guid Tenant, Guid Instance)> SetupManagementRead(WebApplicationFactory<Program> host, HttpClient owner, bool preferences, bool read = true, bool attachmentRead = false)
    {
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
        JsonElement created;
        if (attachmentRead)
        {
            var draft = await Json(await owner.PostAsJsonAsync(api + "/agent-definitions/from-template", new { templateId = "general-assistant", version = 32, expectedRevision = await Revision(owner, tenant) }));
            var candidate = System.Text.Json.Nodes.JsonNode.Parse(draft.GetProperty("candidate").GetRawText())!;
            candidate["environment"]!["capabilities"]!["resolvedCapabilities"]!.AsArray().Add(ToolCatalog.AttachmentsRead);
            var draftId = draft.GetProperty("draftId").GetGuid();
            var saved = await owner.PutAsJsonAsync(api + $"/definition-drafts/{draftId}", new { candidate, expectedRevision = await Revision(owner, tenant), expectedDraftRevision = 1 });
            Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/definition-drafts/{draftId}/publish", new { expectedRevision = await Revision(owner, tenant), expectedDraftRevision = 2 })).StatusCode);
            created = await Json(await owner.PostAsJsonAsync(api + "/agents/from-publication", new { definitionId = draft.GetProperty("definitionId").GetString(), version = 1, expectedRevision = await Revision(owner, tenant) }));
        }
        else created = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 32, expectedRevision = await Revision(owner, tenant) }));
        var instance = created.GetProperty("instance").GetProperty("instanceId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + $"/agents/{instance}/self-management", new {
            expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1,
            overrides = new { mode = new { value = "Managed" }, scopes = new { value = new[] { "Settings", "AutomationPreferences" } }, autoApply = new { value = new[] { "ConversationPreferences" } } }
        })).StatusCode);
        var agent = (await host.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!;
        var state = (await host.Services.GetRequiredService<IIamStore>().ReadTenantAsync(tenant))!; var actor = Assert.Single(state.Users).PrincipalId;
        var access = host.Services.GetRequiredService<TenantAccessService>();
        var role = await access.SaveRoleAsync(actor, tenant, null, "Exact management reader", read ? preferences ? ["agents.read", "agents.update"] : ["agents.read"] : ["agents.update"], await Revision(owner, tenant));
        await access.AssignRoleAsync(actor, tenant, agent.PrincipalId, role.RoleId, AccessScope.ForResource(tenant, "agent", instance.ToString("D")), await Revision(owner, tenant));
        return (tenant, instance);
    }
    private static async Task<AgentRun> AwaitManagementRun(ITenantRunStore runs, Guid tenant, Guid runId, bool pending = false)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        while (true)
        {
            var run = (await runs.FindTenantRunAsync(tenant, runId, deadline.Token))!;
            if (run.IsTerminal || pending && run.Status == AgentRunStatus.WaitingForApproval) return run;
            await Task.Delay(25, deadline.Token);
        }
    }
    [Theory]
    [InlineData(false, "management.inspect", null, false)] [InlineData(true, "management.inspect", null, false)]
    [InlineData(false, "settings.list", null, false)] [InlineData(true, "settings.list", null, false)]
    [InlineData(false, "settings.inspect", "conversationPolicy", false)] [InlineData(true, "settings.inspect", "conversationPolicy", false)]
    [InlineData(false, "automation.preferences.inspect", null, false)] [InlineData(true, "automation.preferences.inspect", null, false)]
    [InlineData(false, "settings.inspect", "conversationPolicy", true)] [InlineData(true, "settings.inspect", "conversationPolicy", true)]
    [InlineData(false, "settings.inspect", "instructions", false)] [InlineData(true, "settings.inspect", "instructions", false)]
    [InlineData(false, "settings.inspect", "behaviorPolicy", false)] [InlineData(true, "settings.inspect", "behaviorPolicy", false)]
    [InlineData(false, "settings.inspect", "initiativePolicy", false)] [InlineData(true, "settings.inspect", "initiativePolicy", false)]
    [InlineData(false, "settings.inspect", "voice", false)] [InlineData(true, "settings.inspect", "voice", false)]
    [InlineData(false, "settings.inspect", "modelDefaults", false)] [InlineData(true, "settings.inspect", "modelDefaults", false)]
    [InlineData(false, "settings.inspect", "memoryPolicy", false)] [InlineData(true, "settings.inspect", "memoryPolicy", false)]
    [InlineData(false, "settings.inspect", "capabilities", false)] [InlineData(true, "settings.inspect", "capabilities", false)]
    [InlineData(false, "settings.inspect", "triggerPolicy", false)] [InlineData(true, "settings.inspect", "triggerPolicy", false)]
    [InlineData(false, "settings.inspect", "providerPreferences", false)] [InlineData(true, "settings.inspect", "providerPreferences", false)]
    [InlineData(false, "settings.inspect", "sandboxPolicy", false)] [InlineData(true, "settings.inspect", "sandboxPolicy", false)]
    [InlineData(false, "settings.inspect", "stateIsolationPolicy", false)] [InlineData(true, "settings.inspect", "stateIsolationPolicy", false)]
    public async Task Canonical_management_reads_use_lazy_discovery_current_sources_and_own_preference_receipts(bool sqlite, string tool, string? section, bool preferences)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-management-read-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new ManagementReadModel(tool, section) { Preferences = preferences };
            await using WebApplicationFactory<Program> factory = sqlite ? new TenantApprovalSqliteFactory(root, model) : new ApprovalExecutionFactory();
            await using var host = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.Replace(ServiceDescriptor.Singleton<ILanguageModel>(model))));
            using var owner = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            var (tenant, instance) = await SetupManagementRead(host, owner, preferences); var api = $"/api/v2/tenants/{tenant}";
            var created = await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 2 })); var session = created.GetProperty("sessionId").GetGuid();
            var response = await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Inspect the currently delegated settings.", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode); var runId = (await Json(response)).GetProperty("runId").GetGuid();
            if (factory is ApprovalExecutionFactory memory) Volatile.Write(ref memory.Enabled, true);
            var final = await AwaitManagementRun(host.Services.GetRequiredService<ITenantRunStore>(), tenant, runId);
            Assert.True(final.Status == AgentRunStatus.Completed, $"{final.Status}: {final.Failure?.Code}: {final.Failure?.Summary}");
            Assert.Equal(1, final.CapabilityLoadCount); Assert.Contains(tool, final.LoadedCapabilityIds);
            if (tool == "settings.inspect" && section == "capabilities" && !preferences)
            {
                var firstRequestOfNextRun = model.Requests.Count;
                var currentSession = (await host.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(tenant, session))!;
                var next = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Inspect the current capabilities again.", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = currentSession.Snapshot.Revision }));
                var nextRun = await AwaitManagementRun(host.Services.GetRequiredService<ITenantRunStore>(), tenant, next.GetProperty("runId").GetGuid());
                Assert.Equal(AgentRunStatus.Completed, nextRun.Status); Assert.Equal(1, nextRun.CapabilityLoadCount); Assert.Contains(tool, nextRun.LoadedCapabilityIds);
                Assert.DoesNotContain(model.Requests.ToArray()[firstRequestOfNextRun].Tools!, item => item.Name == tool);
            }
            var reference = Assert.Single(TenantManagementReadReferences.Read(final.Checkpoint)); Assert.Equal(2, reference.InstanceRevision);
            var requests = model.Requests.ToArray(); Assert.DoesNotContain(requests[0].Tools!, t => t.Name == tool);
            Assert.Contains(requests[1].Tools!, t => t.Name == tool);
            var read = requests.SelectMany(r => r.Messages).Last(m => m.Role == ModelRole.Tool && m.Name == tool);
            using var result = JsonDocument.Parse(read.Text); Assert.True(result.RootElement.GetProperty("tenantManagementRead").GetBoolean());
            var observation = result.RootElement.GetProperty("observation"); Assert.Equal(instance, observation.GetProperty("instanceId").GetGuid()); Assert.Equal(2, observation.GetProperty("instanceRevision").GetInt64());
            Assert.Equal(tool, observation.GetProperty("tool").GetString()); Assert.True(observation.GetProperty("observedSnapshot").GetBoolean());
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(observation.GetRawText()) <= 16000);
            if (section == "sandboxPolicy")
                foreach (var part in new[] { "overrides", "effective", "sources", "definitionDefaults", "constraints" })
                    Assert.All(observation.GetProperty("data").GetProperty(part).EnumerateObject(), field => Assert.Equal("profileId", field.Name));
            var current = (await host.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!.Instance;
            Assert.Equal(preferences ? 3 : 2, current.Revision); if (preferences) Assert.Equal("vi", current.SettingsOverrides!.Language!.Value);
            Assert.Equal(2, final.Admission.Configuration!.InstanceRevision);
            Assert.Empty((await host.Services.GetRequiredService<ITenantApprovalStore>().ListApprovalsAsync(tenant, null, null, new(0,100))).Items);
            var history = await owner.GetAsync(api + $"/sessions/{session}/messages?limit=100"); Assert.Equal(HttpStatusCode.OK, history.StatusCode);
            Assert.Contains("Observed the exact management snapshot", await history.Content.ReadAsStringAsync());
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    [Theory] [InlineData(false, "approve")] [InlineData(true, "approve")] [InlineData(false, "reject")] [InlineData(true, "reject")]
    public async Task Canonical_management_read_Review_suspends_without_values_and_resumes_one_exact_observation(bool sqlite, string decision)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-management-consent-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new ManagementReadModel("settings.inspect", "conversationPolicy");
            await using WebApplicationFactory<Program> factory = sqlite ? new TenantApprovalSqliteFactory(root, model) : new ApprovalExecutionFactory();
            await using var host = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.Replace(ServiceDescriptor.Singleton<ILanguageModel>(model))));
            using var owner = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            var (tenant, instance) = await SetupManagementRead(host, owner, false); var api = $"/api/v2/tenants/{tenant}";
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/agents.read", new { mode = "always", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
            var created = await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 2 })); var session = created.GetProperty("sessionId").GetGuid();
            var response = await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Request the exact settings observation", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode); var runId = (await Json(response)).GetProperty("runId").GetGuid();
            if (factory is ApprovalExecutionFactory memory) Volatile.Write(ref memory.Enabled, true);
            var runs = host.Services.GetRequiredService<ITenantRunStore>(); var pending = await AwaitManagementRun(runs, tenant, runId, true);
            Assert.True(pending.Status == AgentRunStatus.WaitingForApproval, $"{pending.Status}: {pending.Failure}");
            Assert.Empty(TenantManagementReadReferences.Read(pending.Checkpoint));
            Assert.DoesNotContain(model.Requests.SelectMany(r => r.Messages).Where(m => m.Role == ModelRole.Tool), m => m.Text.Contains("\"tenantManagementRead\":true"));
            var approvals = host.Services.GetRequiredService<ITenantApprovalStore>(); var request = (await approvals.FindApprovalAsync(tenant, pending.ApprovalRequestId!.Value))!;
            Assert.Equal(TenantOperationKind.ManagementRead, request.Operation.Kind); Assert.Equal(ApprovalKind.Review, request.Kind); Assert.Equal("agents.read", request.Permission);
            var detail = await owner.GetAsync(api + $"/approvals/{request.RequestId}"); Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
            var view = await Json(detail); Assert.Equal("inspect_management", view.GetProperty("preparedAction").GetProperty("operation").GetString()); Assert.True(view.GetProperty("review").GetProperty("canDecide").GetBoolean());
            Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision, actionHash = request.Operation.ActionHash, expectedRevision = await Revision(owner, tenant), expectedRequestRevision = request.Revision })).StatusCode);
            AgentRun? final = null;
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25)))
                while (true)
                {
                    var all = await runs.ListTenantSessionRunsAsync(tenant, session, null, 0, 100, deadline.Token);
                    final = all.Items.FirstOrDefault(r => r.ApprovalRequestId == request.RequestId && r.AgentRunId != runId);
                    if (final?.IsTerminal == true) break;
                    await Task.Delay(25, deadline.Token);
                }
            Assert.Equal(AgentRunStatus.Completed, final!.Status);
            Assert.Equal(decision == "approve" ? 1 : 0, TenantManagementReadReferences.Read(final.Checkpoint).Count);
            Assert.Null(await approvals.FindOperationGrantAsync(tenant, request.RequestId));
            var receipt = await host.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(tenant, request.Operation.OperationId);
            if (decision == "approve") { Assert.NotNull(receipt); Assert.Equal(TenantManagementReadRules.Completed, receipt.ReasonCode); } else Assert.Null(receipt);
            Assert.Equal(2, (await host.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!.Instance.Revision);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
