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
    public async Task Canonical_management_inspection_pending_compound_consent_survives_SQLite_host_recreation_with_exact_child_sources()
    {
        var root = Path.Combine(Path.GetTempPath(), "management-inspection-recreation-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        Guid tenant = default, instance = default, session = default, runId = default, requestId = default;
        try
        {
            var firstModel = new ManagementReadModel("configuration.inspect");
            await using (var first = new TenantApprovalSqliteFactory(root, firstModel))
            {
                using var owner = first.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
                (tenant, instance) = await SetupManagementInspection(first, owner); var api = $"/api/v2/tenants/{tenant}";
                Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/skills.read", new { mode = "always", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
                var created = await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 6 })); session = created.GetProperty("sessionId").GetGuid();
                var response = await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Keep this exact read consent across restart.", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 });
                Assert.Equal(HttpStatusCode.Accepted, response.StatusCode); runId = (await Json(response)).GetProperty("runId").GetGuid();
                var pending = await AwaitManagementRun(first.Services.GetRequiredService<ITenantRunStore>(), tenant, runId, true); Assert.Equal(AgentRunStatus.WaitingForApproval, pending.Status); Assert.Null(pending.Claim); requestId = pending.ApprovalRequestId!.Value;
                await using var db = await first.Services.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>().CreateDbContextAsync();
                await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync("20261022080000_TenantManagementMutations"));
                // Newer migrations may have reverted before the read guard refuses.
                // Explicitly restore the current schema; startup must never auto-adopt a partial downgrade.
                await db.GetService<IMigrator>().MigrateAsync();
                Assert.False(db.Database.HasPendingModelChanges());
                Assert.Equal(TenantApprovalStatus.Pending, (await first.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, requestId))!.Status);
                Assert.Empty(TenantManagementReadReferences.Read(pending.Checkpoint));
            }
            var secondModel = new ManagementReadModel("configuration.inspect");
            await using (var second = new TenantApprovalSqliteFactory(root, secondModel))
            {
                using var owner = second.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false }); await Login(owner, "owner@example.test", "explicit-synthetic-test-password"); var api = $"/api/v2/tenants/{tenant}";
                var approvals = second.Services.GetRequiredService<ITenantApprovalStore>(); var request = (await approvals.FindApprovalAsync(tenant, requestId))!;
                Assert.Equal(TenantApprovalStatus.Pending, request.Status); Assert.NotNull(request.Operation.ManagementRead);
                Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/approvals/{requestId}/decision", new { decision = "approve", actionHash = request.Operation.ActionHash, expectedRevision = await Revision(owner, tenant), expectedRequestRevision = 1 })).StatusCode);
                var runs = second.Services.GetRequiredService<ITenantRunStore>(); AgentRun? final;
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                while (true)
                {
                    final = (await runs.ListTenantSessionRunsAsync(tenant, session, null, 0, 100, deadline.Token)).Items.SingleOrDefault(r => r.ApprovalRequestId == requestId && r.AgentRunId != runId);
                    if (final?.IsTerminal == true) break; await Task.Delay(25, deadline.Token);
                }
                Assert.True(final.Status == AgentRunStatus.Completed, $"{final.Status}: {final.Failure}");
                var sources = TenantManagementReadReferences.Read(final.Checkpoint); Assert.Single(sources); Assert.NotEmpty(TenantManagementInspectionSources.Read(sources[0].SourcesJson));
                var reopened = new AgentCore.Infrastructure.Security.SqliteIamStore(second.Services.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>(), builtIns: second.Services.GetRequiredService<IBuiltInAgentDefinitionStore>(), inheritedResources: second.Services.GetRequiredService<AgentCore.Application.Agents.InheritedDefinitionResourceCatalog>(), managementInspection: () => second.Services.GetRequiredService<TenantManagementInspectionProjection>());
                await reopened.RequireTenantManagementReadSourcesAsync(new(tenant, final.AgentRunId), sources, null);
                Assert.Null(await approvals.FindOperationGrantAsync(tenant, requestId));
                var result = await second.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(tenant, request.Operation.OperationId);
                Assert.Equal(TenantManagementReadRules.Completed, result!.ReasonCode);
                Assert.Equal(6, (await second.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!.Instance.Revision);
                Assert.Single(secondModel.Requests); Assert.DoesNotContain(secondModel.Requests.Single().Tools!, t => t.Name == "configuration.propose");
            }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
