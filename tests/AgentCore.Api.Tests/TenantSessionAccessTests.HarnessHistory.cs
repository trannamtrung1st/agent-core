using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    public static IEnumerable<object[]> HarnessHistoryCases =>
        from tool in new[] { "harness.instructions.update", "management.policy.update" }
        from approved in new[] { false, true }
        from authorized in new[] { false, true }
        from decision in new[] { "approve", "reject" }
        select new object[] { tool, approved, authorized, decision };
    [Theory(Timeout = 120000)] [MemberData(nameof(HarnessHistoryCases))]
    public async Task Retained_Harness_history_stays_private_and_cannot_replace_current_tenant_consent(string tool, bool historicalApproved, bool executorAuthorized, string decision)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-harness-history-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new SkillAuthoringModel(tool, tool == "management.policy.update" ? "management.inspect" : "harness.inspect", "{}");
            await using var host = new NativeTenantSqliteFactory(root, model);
            using var owner = host.CreateClient();
            var (tenant, instance) = await SetupManagementRead(host, owner, false); var api = $"/api/v2/tenants/{tenant}";
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + $"/agents/{instance}/self-management", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 2,
                overrides = new { mode = new { value = "Managed" }, scopes = new { value = new[] { "Instructions", "ManagementPolicy" } } } })).StatusCode);
            var iam = host.Services.GetRequiredService<IIamStore>(); var human = (await iam.ReadTenantAsync(tenant))!.Users.Single().PrincipalId;
            var access = host.Services.GetRequiredService<TenantAccessService>(); var agents = host.Services.GetRequiredService<ITenantAgentStore>();
            var agent = (await agents.FindTenantAgentAsync(tenant, instance))!;
            var role = await access.SaveRoleAsync(human, tenant, null, "Current Harness author", executorAuthorized ? ["agents.read", "agents.update"] : ["agents.read"], await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, agent.PrincipalId, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
            const string sentinel = "HISTORICAL-PRIVATE-HARNESS-BODY";
            var historicalApproval = Guid.NewGuid();
            var preparation = new HarnessPreparation(Guid.NewGuid(), Guid.NewGuid(), 32, sentinel, 1, historicalApproved ? HarnessPreparationStatus.Ready : HarnessPreparationStatus.AwaitingApproval,
                [new(historicalApproval, new string('a', 64), new("instructions.update", 1, Content: sentinel), historicalApproved ? "Approved" : "Pending")], []);
            // Installed retained data only. Public/canonical writers may not create this history.
            await using (var db = await host.Services.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>().CreateDbContextAsync())
            {
                var row = await db.AgentInstances.SingleAsync(r => r.InstanceId == instance.ToString("D"));
                var state = JsonNode.Parse(row.HarnessManagementJson!)!;
                if (historicalApproved)
                {
                    // Retained full-policy choices are configuration, not an alternate executor.
                    var baseline = (await host.Services.GetRequiredService<IBuiltInAgentDefinitionStore>().GetAsync("general-assistant", 32))!;
                    state["policy"] = JsonSerializer.SerializeToNode(ManagementPolicyResolver.Resolve(baseline, agent.Instance), new JsonSerializerOptions(JsonSerializerDefaults.Web));
                    state["overrides"] = null;
                }
                state["preparation"] = JsonSerializer.SerializeToNode(preparation, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                row.HarnessManagementJson = state.ToJsonString(); await db.SaveChangesAsync();
            }
            var original = (await agents.FindTenantAgentAsync(tenant, instance))!.Instance;
            var historyBytes = JsonSerializer.Serialize(original.HarnessManagement!.Preparation);
            Assert.Contains(sentinel, historyBytes);
            var publicRoutes = new[] { "/agents", $"/agents/{instance}", $"/agents/{instance}/editor", $"/agents/{instance}/configuration-controls",
                $"/agents/{instance}/settings", $"/agents/{instance}/self-management", $"/agents/{instance}/versions", $"/agents/{instance}/active-version/preview?version=32",
                $"/agents/{instance}/state-isolation", $"/agents/{instance}/execution-budgets", $"/agents/{instance}/skills/editor", $"/agents/{instance}/resources/editor" };
            foreach (var route in publicRoutes)
            {
                var response = await owner.GetAsync(api + route); var body = await response.Content.ReadAsStringAsync();
                Assert.True(response.StatusCode == HttpStatusCode.OK, $"{route}: {response.StatusCode}: {body}");
                Assert.DoesNotContain(sentinel, body); Assert.DoesNotContain(historicalApproval.ToString("D"), body);
                Assert.DoesNotContain(preparation.PreparationId.ToString("D"), body);
            }
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync(api + $"/approvals/{historicalApproval}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync(api + $"/approvals/{historicalApproval}/decision", new { decision = "approve", actionHash = new string('a', 64), expectedRevision = await Revision(owner, tenant), expectedRequestRevision = 1 })).StatusCode);
            model.Arguments = tool == "management.policy.update"
                ? JsonSerializer.Serialize(new { expectedInstanceRevision = original.Revision, mode = "Assisted", scopes = new[] { "Instructions", "ManagementPolicy" }, managedAutoApply = Array.Empty<string>() })
                : JsonSerializer.Serialize(new { expectedVersion = 32, expectedInstanceRevision = original.Revision, policyRevision = original.HarnessManagement.PolicyRevision,
                    content = "CURRENT-EXACT-HARNESS-INSTRUCTIONS", expected = "Next Run uses the current reviewed instruction.", observed = "Prepared the current draft, not historical consent." });
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = original.Revision }))).GetProperty("sessionId").GetGuid();
            var sent = await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Review the current exact Harness instruction update.", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 });
            Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode); var initial = (await Json(sent)).GetProperty("runId").GetGuid();
            var runs = host.Services.GetRequiredService<ITenantRunStore>(); var pending = await AwaitManagementRun(runs, tenant, initial, true);
            Assert.True(pending.Status == AgentRunStatus.WaitingForApproval, pending.Failure?.Summary);
            var requests = host.Services.GetRequiredService<ITenantApprovalStore>();
            var request = (await requests.FindApprovalAsync(tenant, pending.ApprovalRequestId!.Value))!;
            Assert.NotEqual(historicalApproval, request.RequestId); Assert.Equal(executorAuthorized ? ApprovalKind.Review : ApprovalKind.ExceptionalGrant, request.Kind);
            Assert.Equal(tool, request.Operation.ManagementMutation!.Harness?.ToolName ?? request.Operation.ManagementMutation.ToolName);
            var publicRequest = await owner.GetAsync(api + $"/approvals/{request.RequestId}");
            Assert.Equal(HttpStatusCode.OK, publicRequest.StatusCode); Assert.DoesNotContain(sentinel, await publicRequest.Content.ReadAsStringAsync());
            var publicInbox = await owner.GetAsync(api + "/approvals");
            Assert.Equal(HttpStatusCode.OK, publicInbox.StatusCode); Assert.DoesNotContain(sentinel, await publicInbox.Content.ReadAsStringAsync());
            Assert.DoesNotContain(sentinel, string.Join("\n", model.Requests.SelectMany(r => r.Messages).Select(m => m.Text)));
            var storedBefore = (await agents.FindTenantAgentAsync(tenant, instance))!.Instance;
            Assert.Equal(original.Revision, storedBefore.Revision); Assert.Equal(historyBytes, JsonSerializer.Serialize(storedBefore.HarnessManagement!.Preparation));
            Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision, actionHash = request.Operation.ActionHash,
                expectedRevision = await Revision(owner, tenant), expectedRequestRevision = request.Revision })).StatusCode);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40)); AgentRun? successor = null;
            while (true)
            {
                successor = (await runs.ListTenantSessionRunsAsync(tenant, session, null, 0, 100, deadline.Token)).Items.FirstOrDefault(r => r.Admission.ApprovalResume?.RequestId == request.RequestId);
                if (successor?.IsTerminal == true) break;
                await Task.Delay(25, deadline.Token);
            }
            Assert.True(successor.Status == AgentRunStatus.Completed, successor.Failure?.Summary);
            var retained = (await agents.FindTenantAgentAsync(tenant, instance))!.Instance;
            Assert.Equal(historyBytes, JsonSerializer.Serialize(retained.HarnessManagement!.Preparation));
            Assert.Equal(decision == "approve" ? original.Revision + 1 : original.Revision, retained.Revision);
            if (tool == "harness.instructions.update")
                Assert.Equal(decision == "approve" ? "CURRENT-EXACT-HARNESS-INSTRUCTIONS" : original.SettingsOverrides?.AgentInstructions?.Value, retained.SettingsOverrides?.AgentInstructions?.Value);
            else
                Assert.Equal(decision == "approve" ? HarnessManagementMode.Assisted : HarnessManagementMode.Managed,
                    ManagementPolicyResolver.Resolve((await host.Services.GetRequiredService<IBuiltInAgentDefinitionStore>().GetAsync("general-assistant", 32))!, retained).Mode);
            var savedRequest = (await requests.FindApprovalAsync(tenant, request.RequestId))!;
            Assert.Equal(request.Operation.ActionHash, savedRequest.Operation.ActionHash);
            Assert.Equal(decision == "approve" ? TenantApprovalStatus.Approved : TenantApprovalStatus.Rejected, savedRequest.Status);
            Assert.DoesNotContain(sentinel, string.Join("\n", model.Requests.SelectMany(r => r.Messages).Select(m => m.Text)));
            Assert.Equal(0, host.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
