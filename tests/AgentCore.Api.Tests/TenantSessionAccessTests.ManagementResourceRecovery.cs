using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
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
    public async Task Resource_authoring_exact_pending_source_and_effect_survive_SQLite_host_recreation_and_refuse_lossy_downgrade()
    {
        var root = Path.Combine(Path.GetTempPath(), "resource-authoring-recovery-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        Guid tenant = default, instance = default, session = default, initial = default, requestId = default; TenantOperationReceipt receipt = null!;
        var arguments = JsonSerializer.Serialize(new { expectedInstanceRevision = 3, logicalPath = "recovered.txt", kind = "Reference", mediaType = "text/plain", content = "DURABLE-REVIEWED-RESOURCE-TEXT", source = "conversation:user" });
        try
        {
            var firstModel = new SkillAuthoringModel("resources.upsert", "management.inspect", "{}") { Arguments = arguments };
            await using (var first = new NativeTenantSqliteFactory(root, firstModel))
            {
                using var owner = first.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
                (tenant, instance) = await SetupManagementRead(first, owner, false); var api = $"/api/v2/tenants/{tenant}";
                Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + $"/agents/{instance}/self-management", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 2,
                    overrides = new { mode = new { value = "Managed" }, scopes = new { value = new[] { "Resources", "Settings" } }, autoApply = new { value = Array.Empty<string>() } } })).StatusCode);
                var state = (await first.Services.GetRequiredService<IIamStore>().ReadTenantAsync(tenant))!; var human = state.Users.Single().PrincipalId;
                var agent = (await first.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!; var access = first.Services.GetRequiredService<TenantAccessService>();
                var role = await access.SaveRoleAsync(human, tenant, null, "Resource author", ["agents.read", "resources.read", "resources.manage"], await Revision(owner, tenant));
                await access.AssignRoleAsync(human, tenant, agent.PrincipalId, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
                session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 3 }))).GetProperty("sessionId").GetGuid();
                var sent = await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Save DURABLE-REVIEWED-RESOURCE-TEXT after exact review, including after restart.", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 });
                Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode); initial = (await Json(sent)).GetProperty("runId").GetGuid();
                var pending = await AwaitManagementRun(first.Services.GetRequiredService<ITenantRunStore>(), tenant, initial, true); Assert.Equal(AgentRunStatus.WaitingForApproval, pending.Status); requestId = pending.ApprovalRequestId!.Value;
                var request = (await first.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, requestId))!;
                Assert.Equal(initial, request.Operation.ManagementMutation!.Resource!.Source!.RootRunId);
                await using var db = await first.Services.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>().CreateDbContextAsync();
                await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantApprovalRequests SET PayloadJson=json_set(PayloadJson,'$.Operation.ManagementMutation.Resource.Source.MaterialHash',{new string('f',64)}) WHERE RequestId={requestId}"));
                await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync("20261022120000_TenantPrivateSkillActivation"));
                await db.GetService<IMigrator>().MigrateAsync(); Assert.False(db.Database.HasPendingModelChanges());
            }
            var secondModel = new SkillAuthoringModel("resources.upsert", "management.inspect", "{}") { Arguments = arguments };
            await using (var second = new NativeTenantSqliteFactory(root, secondModel))
            {
                using var owner = second.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false }); await Login(owner, "owner@example.test", "explicit-synthetic-test-password"); var api = $"/api/v2/tenants/{tenant}";
                var request = (await second.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, requestId))!; Assert.Equal(TenantApprovalStatus.Pending, request.Status);
                var approved = await owner.PostAsJsonAsync(api + $"/approvals/{requestId}/decision", new { decision = "approve", actionHash = request.Operation.ActionHash, expectedRevision = await Revision(owner, tenant), expectedRequestRevision = 1 });
                Assert.True(approved.StatusCode == HttpStatusCode.OK, await approved.Content.ReadAsStringAsync());
                var runs = second.Services.GetRequiredService<ITenantRunStore>(); AgentRun done;
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
                while (true)
                {
                    done = (await runs.ListTenantSessionRunsAsync(tenant, session, null, 0, 100, deadline.Token)).Items.FirstOrDefault(r => r.ApprovalRequestId == requestId && r.AgentRunId != initial)!;
                    if (done?.IsTerminal == true) break; await Task.Delay(25, deadline.Token);
                }
                Assert.True(done.Status == AgentRunStatus.Completed, done.Failure?.Summary); Assert.Equal(3, done.Admission.Configuration!.InstanceRevision);
                receipt = (await second.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(tenant, request.Operation.OperationId))!;
                Assert.Equal(TenantOperationOutcome.Completed, receipt.Outcome); Assert.NotNull(receipt.ManagementResource);
                Assert.Equal(4, (await second.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!.Instance.Revision);
                Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync(api + $"/approvals/{requestId}/decision", new { decision = "approve", actionHash = request.Operation.ActionHash, expectedRevision = await Revision(owner, tenant), expectedRequestRevision = 1 })).StatusCode);
                Assert.Single(secondModel.Requests);
                Assert.Equal("DURABLE-REVIEWED-RESOURCE-TEXT", await owner.GetStringAsync(api + $"/agents/{instance}/resources/" + Uri.EscapeDataString(receipt.ManagementResource!.Result.Key) + "/content"));
                await using var db = await second.Services.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>().CreateDbContextAsync();
                await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantOperationReceipts SET ManagementResourceJson=json_set(ManagementResourceJson,'$.Result.LogicalPath','forged.txt') WHERE OperationId={receipt.OperationId}"));
                await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantOperationReceipts SET ReasonCode='forged' WHERE OperationId={receipt.OperationId}"));
            }
            await using (var third = new NativeTenantSqliteFactory(root, new SkillAuthoringModel("resources.upsert", "management.inspect", "{}") { Arguments = arguments }))
            {
                using var owner = third.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false }); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
                var loaded = (await third.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(tenant, receipt.OperationId))!;
                Assert.Equal(JsonSerializer.Serialize(receipt), JsonSerializer.Serialize(loaded));
                var source = (await third.Services.GetRequiredService<ITenantAgentStore>().ReadTenantAgentConfigurationAsync(tenant, instance))!; Assert.Single(source.Resources.InstanceResources);
                Assert.Equal(4, source.Agent.Instance.Revision);
                Assert.Equal("DURABLE-REVIEWED-RESOURCE-TEXT", await owner.GetStringAsync($"/api/v2/tenants/{tenant}/agents/{instance}/resources/" + Uri.EscapeDataString(loaded.ManagementResource!.Result.Key) + "/content"));
                Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/v2/tenants/{tenant}/approvals/{requestId}")).StatusCode);
            }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
