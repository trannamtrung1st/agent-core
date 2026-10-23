using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Infrastructure.Security;
using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
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
    [Theory(Timeout = 120000)] [InlineData(false)] [InlineData(true)]
    public async Task Private_Skill_sources_and_exact_pending_read_consent_survive_actual_SQLite_host_recreation_without_reactivation_or_lossy_downgrade(bool review)
    {
        var root = Path.Combine(Path.GetTempPath(), "private-skill-recovery-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            Guid tenant, instance, session, runId; Guid? requestId = null;
            await using (var first = new TenantApprovalSqliteFactory(root, new PrivateSkillModel(false)))
            {
                using var owner = first.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
                (tenant, instance) = await SetupManagementRead(first, owner, false); var api = $"/api/v2/tenants/{tenant}";
                Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + $"/agents/{instance}/self-management", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 2,
                    overrides = new { mode = new { value = "Disabled" } } })).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/agents/{instance}/skills/commands", new { operation = "create", expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 3,
                    input = new { id = "private-procedure", name = "Private procedure", description = "Private description sentinel", procedure = PrivateSkillProcedure,
                        projection = "OnDemand", enabled = true, requiredCapabilities = new[] { "chat.respond" } } })).StatusCode);
                var agent = (await first.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!;
                var state = (await first.Services.GetRequiredService<IIamStore>().ReadTenantAsync(tenant))!; var human = Assert.Single(state.Users).PrincipalId;
                var access = first.Services.GetRequiredService<TenantAccessService>();
                var role = await access.SaveRoleAsync(human, tenant, null, "Exact private procedure source", ["skills.read"], await Revision(owner, tenant));
                await access.AssignRoleAsync(human, tenant, agent.PrincipalId, role.RoleId, AccessScope.ForResource(tenant, "skill", instance.ToString("D") + ":" + PrivateSkillKey), await Revision(owner, tenant));
                if (review) Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/skills.read", new { mode = "always", independentReview = false,
                    expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
                session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 4 }))).GetProperty("sessionId").GetGuid();
                var response = await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Use the exact pinned private-procedure Skill.", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 });
                Assert.Equal(HttpStatusCode.Accepted, response.StatusCode); runId = (await Json(response)).GetProperty("runId").GetGuid();
                var run = await AwaitManagementRun(first.Services.GetRequiredService<ITenantRunStore>(), tenant, runId, true);
                Assert.Equal(review ? AgentRunStatus.WaitingForApproval : AgentRunStatus.Completed, run.Status); requestId = run.ApprovalRequestId;
                Assert.Equal(review ? 0 : 1, run.SkillLoadCount);
                if (!review) Assert.Equal(ToolCatalog.SkillsLoad, Assert.Single(TenantManagementReadReferences.Read(run.Checkpoint)).ToolName);
                await using var db = await first.Services.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>().CreateDbContextAsync();
                if (review)
                {
                    var request = (await first.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, requestId!.Value))!;
                    var other = Assert.Single(run.PinnedSkillCatalog.Where(s => s.Origin != AgentCore.Domain.Definitions.SkillOrigin.BuiltIn && s.Key != PrivateSkillKey).Take(1));
                    foreach (var malformed in new[] { "null_kind", "duplicate_witness" })
                    {
                        var invalid = TenantApprovalRecord.From(request with { RequestId = Guid.NewGuid() });
                        var payload = JsonNode.Parse(invalid.PayloadJson)!.AsObject(); var read = payload["Operation"]!["ManagementRead"]!;
                        var sources = JsonNode.Parse(read["SourcesJson"]!.GetValue<string>())!.AsArray();
                        if (malformed == "null_kind") sources[0]!["Kind"] = null;
                        else
                        {
                            sources.Add(sources[0]!.DeepClone());
                            read["ArgumentsJson"] = JsonSerializer.Serialize(new { ids = new[] { PrivateSkillKey, other.Key } });
                        }
                        read["SourcesJson"] = sources.ToJsonString(); invalid.PayloadJson = payload.ToJsonString(); db.Add(invalid);
                        var rejected = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
                        Assert.Contains("Private Skill consent requires exact owned pinned procedures", rejected.InnerException!.Message);
                        db.Entry(invalid).State = EntityState.Detached;
                    }
                }
                var currentMigrations = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
                await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync("20261022110000_TenantManagementSkillAuthoring"));
                Assert.Contains("20261022120000_TenantPrivateSkillActivation", await db.Database.GetAppliedMigrationsAsync());
                // Later safe Down steps may commit before the retained-source guard refuses.
                // Restore the exact current schema before exercising current-model reopen.
                await db.GetService<IMigrator>().MigrateAsync();
                Assert.Equal(currentMigrations, (await db.Database.GetAppliedMigrationsAsync()).ToArray());
                Assert.False(db.Database.HasPendingModelChanges());
            }
            var model = new PrivateSkillModel(false);
            await using var second = new TenantApprovalSqliteFactory(root, model);
            using var reopened = second.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            await Login(reopened, "owner@example.test", "explicit-synthetic-test-password"); var prefix = $"/api/v2/tenants/{tenant}";
            var runs = second.Services.GetRequiredService<ITenantRunStore>(); var saved = (await runs.FindTenantRunAsync(tenant, runId))!;
            if (review)
            {
                Assert.Equal(AgentRunStatus.WaitingForApproval, saved.Status); Assert.Equal(0, saved.SkillLoadCount);
                var approvals = second.Services.GetRequiredService<ITenantApprovalStore>(); var request = (await approvals.FindApprovalAsync(tenant, requestId!.Value))!;
                var detail = await reopened.GetAsync(prefix + $"/approvals/{request.RequestId}"); Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
                Assert.Contains(PrivateSkillProcedure, await detail.Content.ReadAsStringAsync());
                Assert.Equal(HttpStatusCode.OK, (await reopened.PostAsJsonAsync(prefix + $"/approvals/{request.RequestId}/decision", new { decision = "approve", actionHash = request.Operation.ActionHash,
                    expectedRevision = await Revision(reopened, tenant), expectedRequestRevision = request.Revision })).StatusCode);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                while (true)
                {
                    saved = (await runs.ListTenantSessionRunsAsync(tenant, session, null, 0, 100, deadline.Token)).Items.FirstOrDefault(r => r.AgentRunId != runId && r.ApprovalRequestId == request.RequestId)!;
                    if (saved?.IsTerminal == true) break; await Task.Delay(25, deadline.Token);
                }
                Assert.Null(await approvals.FindOperationGrantAsync(tenant, request.RequestId));
            }
            else Assert.Empty(model.Requests);
            Assert.True(saved.Status == AgentRunStatus.Completed, $"{saved.Status}: {saved.Failure}"); Assert.Equal(1, saved.SkillLoadCount);
            Assert.Contains(PrivateSkillKey, saved.ActiveSkillKeys); Assert.Equal(4, saved.Admission.Configuration!.InstanceRevision);
            Assert.Contains(saved.PinnedSkillCatalog, s => s.Key == PrivateSkillKey && s.Procedure == PrivateSkillProcedure);
            Assert.Equal(HttpStatusCode.OK, (await reopened.GetAsync(prefix + $"/sessions/{session}/messages?limit=100")).StatusCode);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
