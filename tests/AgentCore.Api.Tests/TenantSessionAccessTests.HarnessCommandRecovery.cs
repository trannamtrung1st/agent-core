using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;
public sealed partial class TenantSessionAccessTests
{
    [Fact(Timeout = 120000)]
    public async Task Harness_command_pending_exact_assessment_and_effect_survive_SQLite_recreation_and_refuse_lossy_downgrade()
    {
        var root = Path.Combine(Path.GetTempPath(), "harness-recovery-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        Guid tenant = default, instance = default, session = default, initial = default, requestId = default;
        string arguments = "{}"; TenantOperationReceipt receipt = null!; HarnessManagementState history = null!;
        try
        {
            var model = new SkillAuthoringModel("harness.instructions.update", "harness.inspect", "{}");
            await using (var first = new NativeTenantSqliteFactory(root, model))
            {
                using var owner = first.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
                (tenant, instance) = await SetupManagementRead(first, owner, false); var api = $"/api/v2/tenants/{tenant}";
                Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + $"/agents/{instance}/self-management", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 2,
                    overrides = new { mode = new { value = "Managed" }, scopes = new { value = new[] { "Instructions" } } } })).StatusCode);
                var security = (await first.Services.GetRequiredService<IIamStore>().ReadTenantAsync(tenant))!; var human = security.Users.Single().PrincipalId;
                var agent = (await first.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!;
                var access = first.Services.GetRequiredService<TenantAccessService>();
                var role = await access.SaveRoleAsync(human, tenant, null, "Durable Harness author", ["agents.read", "agents.update"], await Revision(owner, tenant));
                await access.AssignRoleAsync(human, tenant, agent.PrincipalId, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
                arguments = model.Arguments = JsonSerializer.Serialize(new { expectedVersion = 1, expectedInstanceRevision = 3, policyRevision = agent.Instance.HarnessManagement!.PolicyRevision,
                    content = "DURABLE-EXACT-HARNESS-INSTRUCTIONS", expected = "Next Run uses this literal instruction.", observed = "Prepared a local draft only." });
                session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 3 }))).GetProperty("sessionId").GetGuid();
                var sent = await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Review and retain DURABLE-EXACT-HARNESS-INSTRUCTIONS.", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 });
                Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode); initial = (await Json(sent)).GetProperty("runId").GetGuid();
                var pending = await AwaitManagementRun(first.Services.GetRequiredService<ITenantRunStore>(), tenant, initial, true);
                Assert.Equal(AgentRunStatus.WaitingForApproval, pending.Status); requestId = pending.ApprovalRequestId!.Value;
                await using var db = await first.Services.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>().CreateDbContextAsync();
                await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantApprovalRequests SET PayloadJson=json_set(PayloadJson,'$.Operation.ManagementMutation.Harness.Assessment.Evidence.Status',0) WHERE RequestId={requestId}"));
                await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantApprovalRequests SET PayloadJson=json_set(PayloadJson,'$.Operation.ManagementMutation.Harness.ArgumentsJson','{{}}') WHERE RequestId={requestId}"));
                var stored = await db.Set<AgentCore.Infrastructure.Security.TenantApprovalRecord>().AsNoTracking().SingleAsync(r => r.RequestId == requestId);
                var columns = typeof(AgentCore.Infrastructure.Security.TenantApprovalRecord).GetProperties().Select(p => p.Name).ToArray();
                foreach (var boundary in new[] { "extra_argument", "substituted_content", "wider_section", "verified_assessment" })
                {
                    var nextId = Guid.NewGuid(); var payload = JsonNode.Parse(stored.PayloadJson)!; payload["RequestId"] = nextId;
                    var mutation = payload["Operation"]!["ManagementMutation"]!; var harness = mutation["Harness"]!;
                    var original = JsonNode.Parse(harness["ArgumentsJson"]!.GetValue<string>())!;
                    if (boundary == "extra_argument") original["publish"] = true;
                    if (boundary == "substituted_content") original["content"] = "substituted instruction";
                    if (boundary == "wider_section") { var canonical = JsonNode.Parse(mutation["ArgumentsJson"]!.GetValue<string>())!; canonical["section"] = "capabilities"; mutation["ArgumentsJson"] = canonical.ToJsonString(); }
                    if (boundary == "verified_assessment") harness["Assessment"]!["Evidence"]!["Status"] = 0;
                    harness["ArgumentsJson"] = original.ToJsonString();
                    using var insert = db.Database.GetDbConnection().CreateCommand(); await db.Database.OpenConnectionAsync();
                    insert.CommandText = "INSERT INTO TenantApprovalRequests (" + string.Join(",", columns.Select(c => "\"" + c + "\"")) + ") SELECT "
                        + string.Join(",", columns.Select(c => c == "RequestId" ? "$id" : c == "PayloadJson" ? "$payload" : "\"" + c + "\"")) + " FROM TenantApprovalRequests WHERE RequestId=$old";
                    var parameter = insert.CreateParameter(); parameter.ParameterName = "$id"; parameter.Value = nextId; insert.Parameters.Add(parameter);
                    parameter = insert.CreateParameter(); parameter.ParameterName = "$payload"; parameter.Value = payload.ToJsonString(); insert.Parameters.Add(parameter);
                    parameter = insert.CreateParameter(); parameter.ParameterName = "$old"; parameter.Value = requestId; insert.Parameters.Add(parameter);
                    var refused = await Assert.ThrowsAsync<SqliteException>(() => insert.ExecuteNonQueryAsync());
                    Assert.Contains("Harness command requires closed exact submitted fields and assessment", refused.Message);
                }
                await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync("20261023100000_TenantManagementResourceAuthoring"));
                await db.GetService<IMigrator>().MigrateAsync(); Assert.False(db.Database.HasPendingModelChanges());
            }
            var resumedModel = new SkillAuthoringModel("harness.instructions.update", "harness.inspect", "{}") { Arguments = arguments };
            await using (var second = new NativeTenantSqliteFactory(root, resumedModel))
            {
                using var owner = second.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false }); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
                var api = $"/api/v2/tenants/{tenant}";
                var request = (await second.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, requestId))!;
                Assert.Equal(TenantApprovalStatus.Pending, request.Status); Assert.Equal(TenantManagementMutationContract.Parse("harness.instructions.update", arguments).Json, request.Operation.ManagementMutation!.Harness!.ArgumentsJson);
                var approved = await owner.PostAsJsonAsync(api + $"/approvals/{requestId}/decision", new { decision = "approve", actionHash = request.Operation.ActionHash, expectedRevision = await Revision(owner, tenant), expectedRequestRevision = 1 });
                Assert.True(approved.StatusCode == HttpStatusCode.OK, await approved.Content.ReadAsStringAsync());
                var runs = second.Services.GetRequiredService<ITenantRunStore>(); AgentRun done;
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while (true) { done = (await runs.ListTenantSessionRunsAsync(tenant, session, null, 0, 100, deadline.Token)).Items.FirstOrDefault(r => r.AgentRunId != initial && r.ApprovalRequestId == requestId)!;
                    if (done?.IsTerminal == true) break; await Task.Delay(25, deadline.Token); }
                Assert.True(done.Status == AgentRunStatus.Completed, done.Failure?.Summary); Assert.Equal(3, done.Admission.Configuration!.InstanceRevision);
                receipt = (await second.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(tenant, request.Operation.OperationId))!;
                Assert.Equal(TenantOperationOutcome.Completed, receipt.Outcome);
                var updated = (await second.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!.Instance;
                history = updated.HarnessManagement!; Assert.Equal(request.Operation.ManagementMutation!.Harness!.Assessment, Assert.Single(history.InstanceChanges!));
                Assert.Equal("DURABLE-EXACT-HARNESS-INSTRUCTIONS", updated.SettingsOverrides!.AgentInstructions!.Value); Assert.Equal(4, updated.Revision);
                Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync(api + $"/approvals/{requestId}/decision", new { decision = "approve", actionHash = request.Operation.ActionHash, expectedRevision = await Revision(owner, tenant), expectedRequestRevision = 1 })).StatusCode);
                Assert.Single(resumedModel.Requests);
                await using var db = await second.Services.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>().CreateDbContextAsync();
                await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantOperationReceipts SET ReasonCode='forged' WHERE OperationId={receipt.OperationId}"));
            }
            await using (var third = new NativeTenantSqliteFactory(root, new SkillAuthoringModel("harness.instructions.update", "harness.inspect", "{}") { Arguments = arguments }))
            {
                using var owner = third.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false }); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
                var reloaded = (await third.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!.Instance;
                Assert.Equal(4, reloaded.Revision); Assert.Equal(JsonSerializer.Serialize(history), JsonSerializer.Serialize(reloaded.HarnessManagement));
                Assert.Equal(JsonSerializer.Serialize(receipt), JsonSerializer.Serialize(await third.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(tenant, receipt.OperationId)));
                Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/v2/tenants/{tenant}/approvals/{requestId}")).StatusCode);
            }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
