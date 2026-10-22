using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;
public sealed partial class TenantSessionAccessTests
{
    [Fact]
    public async Task Management_mutation_pending_exact_consent_survives_SQLite_host_recreation_without_replaying_effects()
    {
        var root = Path.Combine(Path.GetTempPath(), "management-mutation-recreation-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        Guid tenant = default, instance = default, session = default, runId = default, requestId = default;
        try
        {
            var firstModel = new ManagementMutationModel("settings.patch") { Arguments = ManagementCommand("settings.patch", "") };
            await using (var first = new TenantApprovalSqliteFactory(root, firstModel))
            {
                using var owner = first.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
                (tenant, instance) = await SetupManagementMutation(first, owner); var api = $"/api/v2/tenants/{tenant}";
                var created = await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 3 })); session = created.GetProperty("sessionId").GetGuid();
                var response = await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Keep this exact configuration change consent across restart.", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 });
                Assert.Equal(HttpStatusCode.Accepted, response.StatusCode); runId = (await Json(response)).GetProperty("runId").GetGuid();
                var pending = await AwaitManagementRun(first.Services.GetRequiredService<ITenantRunStore>(), tenant, runId, true); Assert.Equal(AgentRunStatus.WaitingForApproval, pending.Status); Assert.Null(pending.Claim); requestId = pending.ApprovalRequestId!.Value;
                await using var db = await first.Services.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>().CreateDbContextAsync();
                await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync("20261022070000_TenantManagementReadConsent"));
                await db.GetService<IMigrator>().MigrateAsync();
                Assert.Equal(TenantApprovalStatus.Pending, (await first.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, requestId))!.Status);
                Assert.Single(TenantManagementReadReferences.Read(pending.Checkpoint));
            }
            var secondModel = new ManagementMutationModel("settings.patch") { Arguments = ManagementCommand("settings.patch", "") };
            await using (var second = new TenantApprovalSqliteFactory(root, secondModel))
            {
                using var owner = second.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false }); await Login(owner, "owner@example.test", "explicit-synthetic-test-password"); var api = $"/api/v2/tenants/{tenant}";
                var approvals = second.Services.GetRequiredService<ITenantApprovalStore>(); var request = (await approvals.FindApprovalAsync(tenant, requestId))!;
                Assert.Equal(TenantApprovalStatus.Pending, request.Status); Assert.NotNull(request.Operation.ManagementMutation);
                Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/approvals/{requestId}/decision", new { decision = "approve", actionHash = request.Operation.ActionHash, expectedRevision = await Revision(owner, tenant), expectedRequestRevision = 1 })).StatusCode);
                var runs = second.Services.GetRequiredService<ITenantRunStore>(); AgentRun? final;
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                while (true)
                {
                    final = (await runs.ListTenantSessionRunsAsync(tenant, session, null, 0, 100, deadline.Token)).Items.SingleOrDefault(r => r.ApprovalRequestId == requestId && r.AgentRunId != runId);
                    if (final?.IsTerminal == true) break; await Task.Delay(25, deadline.Token);
                }
                Assert.True(final.Status == AgentRunStatus.Completed, $"{final.Status}: {final.Failure}");
                var sources = TenantManagementReadReferences.Read(final.Checkpoint); Assert.Single(sources);
                var reopened = new AgentCore.Infrastructure.Security.SqliteIamStore(second.Services.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>(), builtIns: second.Services.GetRequiredService<IBuiltInAgentDefinitionStore>());
                await reopened.RequireTenantManagementReadSourcesAsync(new(tenant, final.AgentRunId), sources, null);
                Assert.Null(await approvals.FindOperationGrantAsync(tenant, requestId));
                var result = await second.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(tenant, request.Operation.OperationId);
                Assert.Equal(TenantManagementMutationRules.Completed, result!.ReasonCode);
                Assert.Equal(4, (await second.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!.Instance.Revision);
                Assert.Equal(ManagementInstructions, (await second.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!.Instance.SettingsOverrides!.AgentInstructions!.Value);
                Assert.Equal(3, final.Admission.Configuration!.InstanceRevision);
                Assert.Equal(4, result.InstanceRevision);
                var repeated = await owner.PostAsJsonAsync(api + $"/approvals/{requestId}/decision", new { decision = "approve", actionHash = request.Operation.ActionHash, expectedRevision = await Revision(owner, tenant), expectedRequestRevision = 1 });
                Assert.Equal(HttpStatusCode.Conflict, repeated.StatusCode);
                Assert.Equal(result, await second.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(tenant, request.Operation.OperationId));
                Assert.Equal(4, (await second.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!.Instance.Revision);
                Assert.Single(secondModel.Requests); Assert.DoesNotContain(secondModel.Requests.Single().Tools!, t => t.Name == "configuration.propose");
            }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
