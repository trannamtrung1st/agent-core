using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
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
using Microsoft.Extensions.Logging;

namespace AgentCore.Api.Tests;
public sealed partial class TenantSessionAccessTests
{
    private sealed class HarnessSelectionUnavailable : IToolConfigurationGate
    { public bool IsConfigured(string toolName) => toolName != ToolCatalog.WebFetch; }
    public static IEnumerable<object[]> HarnessCommandCases => from sqlite in new[] { false, true }
        from item in new[] {
            ("create", "direct"), ("update", "direct"), ("inherited_remove", "direct"), ("remove", "approve"), ("remove", "reject"),
            ("create", "assisted"), ("update", "assisted"), ("create", "reject"), ("update", "reject"),
            ("instructions", "approve"), ("instructions", "reject"), ("instructions", "large"), ("instructions", "limit"),
            ("select", "approve"), ("select", "reject"), ("configure", "approve"), ("configure", "reject"), ("configure", "ceiling"),
            ("update", "unchanged"), ("instructions", "unchanged"), ("select", "unchanged"), ("configure", "unchanged"),
            ("update", "no_read"), ("update", "parent_only"), ("update", "grant"), ("update", "policy"),
            ("update", "read_review"), ("update", "independent"), ("remove", "grant"), ("remove", "missing"), ("update", "wrong_scope"), ("select", "uneligible"), ("select", "configured_off"), ("update", "forged_source"), ("instructions", "stale"), ("instructions", "wrong_scope") }
        select new object[] { sqlite, item.Item1, item.Item2 };

    [Theory(Timeout = 120000)] [MemberData(nameof(HarnessCommandCases))]
    public async Task Canonical_Harness_commands_retain_independent_delegation_exact_effect_and_partial_assessment(bool sqlite, string operation, string scenario)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-harness-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var knowledge = operation is "create" or "update" or "remove" or "inherited_remove";
        var tool = knowledge ? operation is "remove" or "inherited_remove" ? "harness.knowledge.remove" : "harness.knowledge.upsert"
            : operation == "instructions" ? "harness.instructions.update" : operation == "select" ? "harness.tool.select" : "harness.tool.configure";
        var model = new SkillAuthoringModel(tool, "harness.inspect", "{}") { SuppressConsent = scenario == "parent_only", ReadConsent = scenario == "read_review" };
        try
        {
            await using WebApplicationFactory<Program> factory = sqlite ? new TenantApprovalSqliteFactory(root, model) : new ApprovalExecutionFactory();
            await using var host = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => { s.Replace(ServiceDescriptor.Singleton<ILanguageModel>(model));
                if (scenario == "configured_off") s.Replace(ServiceDescriptor.Singleton<IToolConfigurationGate>(new HarnessSelectionUnavailable())); }));
            using var owner = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            var logs = new ManagementMutationLogs(); host.Services.GetRequiredService<ILoggerFactory>().AddProvider(logs);
            var (tenant, instance) = await SetupManagementRead(host, owner, false, attachmentRead: operation == "configure");
            var api = $"/api/v2/tenants/{tenant}"; var path = api + $"/agents/{instance}";
            var scope = knowledge ? "KnowledgeResources" : operation == "instructions" ? "Instructions" : "ToolSelection";
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(path + "/self-management", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 2,
                overrides = new { mode = new { value = scenario is "assisted" or "reject" && knowledge ? "Assisted" : "Managed" },
                    scopes = new { value = new[] { scenario == "wrong_scope" ? knowledge ? "Resources" : "Settings" : scope } }, autoApply = new { value = Array.Empty<string>() } } })).StatusCode);
            long revision = 3; string id = "local-note"; string? key = null; JsonElement selected = default;
            if (knowledge && operation != "create" && operation != "inherited_remove" && scenario != "missing")
            {
                var upload = await owner.PostAsJsonAsync(path + "/resources", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = revision,
                    input = new { logicalPath = "knowledge/local-note", kind = 1, mediaType = "text/markdown", content = Encoding.UTF8.GetBytes("ORIGINAL-HARNESS-KNOWLEDGE"), enabled = true } });
                Assert.True(upload.IsSuccessStatusCode, await upload.Content.ReadAsStringAsync()); selected = (await Json(upload)).GetProperty("resource"); revision++;
                key = selected.GetProperty("key").GetString();
            }
            if (operation == "inherited_remove")
            {
                selected = (await Json(await owner.GetAsync(path + "/resources"))).GetProperty("catalog").GetProperty("resources").EnumerateArray()
                    .First(r => r.GetProperty("origin").GetString() == "Definition" && r.GetProperty("kind").GetInt32() == 1);
                key = id = selected.GetProperty("key").GetString()!;
            }
            var content = scenario == "limit" ? new string('i', 7979) + " complete literal end" : scenario == "large" ? new string('i', 31980) + " complete literal end" : "REVISED-HARNESS-CONTENT";
            if (scenario == "limit") Assert.Equal(8000, content.Length);
            if (scenario == "unchanged" && operation == "update") content = "ORIGINAL-HARNESS-KNOWLEDGE";
            if (scenario == "unchanged" && operation is "instructions" or "select" or "configure")
            {
                object set = operation == "instructions" ? (object)new { agentInstructions = content } : operation == "configure" ? new { allowUnreadUnsupportedTypes = false }
                    : new { selected = Array.Empty<string>() }; // populated below from actual catalog for selection
                if (operation == "select")
                {
                    var settings = await Json(await owner.GetAsync(path + "/settings"));
                    var caps = settings.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("section").GetString() == "capabilities");
                    set = new { selected = caps.GetProperty("effective").GetProperty("selected").EnumerateArray().Select(v => v.GetString()).Where(v => v != ToolCatalog.WebFetch).ToArray() };
                }
                var seeded = await owner.PatchAsJsonAsync(path + "/settings/" + (operation == "instructions" ? "instructions" : "capabilities"), new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = revision, set, clear = Array.Empty<string>() });
                Assert.True(seeded.IsSuccessStatusCode, await seeded.Content.ReadAsStringAsync()); revision++;
            }
            var security = (await host.Services.GetRequiredService<IIamStore>().ReadTenantAsync(tenant))!; var human = security.Users.Single().PrincipalId;
            var access = host.Services.GetRequiredService<TenantAccessService>(); var agent = (await host.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!;
            var primary = knowledge ? operation == "remove" ? "resources.delete" : "resources.manage" : "agents.update";
            var permissions = knowledge ? scenario is "no_read" or "missing" ? new[] { "agents.read", primary } : scenario == "parent_only" ? new[] { "agents.read", "agents.update", "resources.read" }
                : scenario == "grant" ? new[] { "agents.read", "resources.read" } : new[] { "agents.read", "resources.read", primary, "sessions.read" }
                : new[] { "agents.read", "agents.update" };
            var role = await access.SaveRoleAsync(human, tenant, null, "Contextual Harness author", permissions, await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, agent.PrincipalId, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
            if (scenario is "policy" or "independent" or "read_review") Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/" + (scenario == "read_review" ? "resources.read" : primary), new { mode = "always", independentReview = scenario == "independent", expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
            var args = new Dictionary<string,object?> { ["expectedVersion"] = agent.Instance.ActiveVersion, ["expectedInstanceRevision"] = scenario == "stale" ? revision - 1 : revision,
                ["policyRevision"] = agent.Instance.HarnessManagement!.PolicyRevision, ["expected"] = "Next Run retains the exact local change.", ["observed"] = "Verified", ["limitation"] = "No production task was executed." };
            if (knowledge) { args["id"] = id; if (operation is "create" or "update") { args["content"] = content; args["source"] = scenario == "forged_source" ? "workspace:/working/unread.txt" : "conversation:user"; } }
            else if (operation == "instructions") args["content"] = content;
            else { args["id"] = operation == "select" ? scenario == "uneligible" ? "harness.inspect" : ToolCatalog.WebFetch : ToolCatalog.AttachmentsRead; args[operation == "select" ? "enabled" : "allowUnreadUnsupportedTypes"] = operation == "configure" && scenario == "ceiling"; }
            model.Arguments = JsonSerializer.Serialize(args);
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = revision }))).GetProperty("sessionId").GetGuid();
            var sent = await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Retain REVISED-HARNESS-CONTENT in this Instance using the exact Harness command.", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 });
            Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode); var initial = (await Json(sent)).GetProperty("runId").GetGuid();
            if (factory is ApprovalExecutionFactory memory) Volatile.Write(ref memory.Enabled, true);
            string Trace() => JsonSerializer.Serialize(model.Requests.SelectMany(r => r.Messages).Where(m => m.Role == ModelRole.Tool).Distinct().Select(m => new { m.Name, m.Text })) + "\n" + string.Join('\n', logs.Errors);
            var waiting = scenario is "approve" or "reject" or "assisted" or "limit" or "grant" or "policy" or "read_review";
            var runs = host.Services.GetRequiredService<ITenantRunStore>(); var approvals = host.Services.GetRequiredService<ITenantApprovalStore>();
            AgentRun done;
            try { done = await AwaitManagementRun(runs, tenant, initial, waiting); }
            catch (OperationCanceledException) { throw new Xunit.Sdk.XunitException(Trace()); }
            var admitted = done; TenantApprovalRequest? request = null;
            if (waiting)
            {
                Assert.True(done.Status == AgentRunStatus.WaitingForApproval, done.Failure?.Summary + Trace());
                request = (await approvals.FindApprovalAsync(tenant, done.ApprovalRequestId!.Value))!;
                if (scenario == "read_review") { Assert.NotNull(request.Operation.ManagementRead); Assert.Equal("agents.read", request.Permission); }
                else { Assert.Equal(tool, request.Operation.ManagementMutation!.Harness!.ToolName); Assert.Equal(primary, request.Permission); }
                Assert.Equal(scenario == "grant" ? ApprovalKind.ExceptionalGrant : ApprovalKind.Review, request.Kind);
                var decision = await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = scenario == "reject" ? "reject" : "approve", actionHash = request.Operation.ActionHash, expectedRevision = await Revision(owner, tenant), expectedRequestRevision = request.Revision });
                Assert.True(decision.StatusCode == HttpStatusCode.OK, await decision.Content.ReadAsStringAsync() + Trace());
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try
                {
                    while (true) { done = (await runs.ListTenantSessionRunsAsync(tenant, session, null, 0, 100, deadline.Token)).Items.FirstOrDefault(r => r.AgentRunId != initial && r.ApprovalRequestId == request.RequestId)!;
                        if (done?.IsTerminal == true) break; await Task.Delay(25, deadline.Token); }
                }
                catch (OperationCanceledException) { throw new Xunit.Sdk.XunitException(Trace()); }
            }
            Assert.True(done.Status == AgentRunStatus.Completed, done.Failure?.Summary + Trace());
            var snapshot = (await host.Services.GetRequiredService<ITenantAgentStore>().ReadTenantAgentConfigurationAsync(tenant, instance))!;
            var changed = scenario is "direct" or "approve" or "assisted" or "limit" or "grant" or "policy" or "read_review";
            Assert.Equal(revision + (changed ? 1 : 0), snapshot.Agent.Instance.Revision);
            Assert.Equal(revision, done.Admission.Configuration!.InstanceRevision);
            var receipt = request is null || scenario == "read_review" ? await ((ITenantDirectOperationStore)host.Services.GetRequiredService<IIamStore>()).FindDirectOperationReceiptAsync(tenant, done.AgentRunId, "write-skill")
                : await host.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(tenant, request.Operation.OperationId);
            if (changed)
            {
                Assert.NotNull(receipt); Assert.Equal(TenantOperationOutcome.Completed, receipt.Outcome);
                var assessment = Assert.Single(snapshot.Agent.Instance.HarnessManagement!.InstanceChanges!);
                Assert.Equal(HarnessEvidenceStatus.PartiallyVerified, assessment.Evidence.Status); Assert.Equal("Verified", assessment.Evidence.Observed);
                Assert.Equal("No production task was executed.", assessment.Evidence.Limitation); Assert.Equal(revision + 1, assessment.InstanceRevision);
                Assert.Equal(ToolActionHash.Compute(tool, JsonSerializer.Deserialize<JsonElement>(model.Arguments)), assessment.ActionHash);
                if (knowledge)
                {
                    Assert.Equal(assessment.OperationId, receipt.ManagementResource!.HarnessChangeId);
                    if (operation == "remove") Assert.Empty(snapshot.Resources.InstanceResources);
                    else if (operation == "inherited_remove") Assert.False(snapshot.Resources.DefinitionStates.Single(s => "definition:" + s.ResourceId.ToString("D") == key).EnabledOverride);
                    else Assert.Equal(content, await owner.GetStringAsync(path + "/resources/" + Uri.EscapeDataString(receipt.ManagementResource.Result.Key) + "/content"));
                    if (key is not null) Assert.NotNull(await host.Services.GetRequiredService<ITenantManagementStore>().ReadRetainedResourcePinAsync(new(tenant, admitted.AgentRunId), admitted.Admission.Configuration!.Resources.Single(r => r.Key == key)));
                }
                else if (operation == "instructions") Assert.Equal(content, snapshot.Agent.Instance.SettingsOverrides!.AgentInstructions!.Value);
                else if (operation == "select") Assert.DoesNotContain(ToolCatalog.WebFetch, snapshot.Agent.Instance.SettingsOverrides!.SelectedCapabilities!.Value);
                else Assert.False(snapshot.Agent.Instance.SettingsOverrides!.AllowUnreadUnsupportedTypes!.Value);
            }
            else { Assert.Empty(snapshot.Agent.Instance.HarnessManagement!.InstanceChanges ?? []); Assert.True(receipt is null || receipt.Outcome != TenantOperationOutcome.Completed, Trace()); }
            if (scenario == "unchanged") Assert.Contains("operation_unchanged", Trace());
            if (scenario == "no_read") { Assert.Contains("resource_read_required", Trace()); Assert.DoesNotContain("ORIGINAL-HARNESS-KNOWLEDGE", Trace()); }
            if (scenario == "forged_source") Assert.Contains("resource_source_unavailable", Trace());
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
