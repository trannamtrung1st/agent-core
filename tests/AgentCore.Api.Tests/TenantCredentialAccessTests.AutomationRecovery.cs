using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Api.Tests;

public sealed partial class TenantCredentialAccessTests
{
    [Theory]
    [InlineData("scheduled")] [InlineData("manual")] [InlineData("archived")]
    public async Task Tenant_owned_Automation_occurrence_reopens_in_a_new_SQLite_host_without_a_profile_or_duplicate_Run(string mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-owned-automation-" + Guid.NewGuid().ToString("N"));
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow); Guid tenant = default, instance = default, automation = default, runId = default, human = default;
        try
        {
            await using (var first = new OAuthFactory(root, sqlite: true, preserve: true, servicesOverride: services => { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock); }))
            {
                using var owner = ConnectedClient(first); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
                tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
                var api = $"/api/v2/tenants/{tenant}"; human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
                instance = (await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 36, expectedRevision = await Revision(owner, tenant) }))).GetProperty("instance").GetProperty("instanceId").GetGuid();
                var agent = (await first.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!;
                var access = first.Services.GetRequiredService<TenantAccessService>();
                var role = await access.SaveRoleAsync(human, tenant, null, "Run owned occurrences", ["automations.run", "sessions.create", "agent-runs.create"], await Revision(owner, tenant));
                await access.AssignRoleAsync(human, tenant, agent.PrincipalId, role.RoleId, AgentCore.Domain.Security.AccessScope.ForTenant(tenant), await Revision(owner, tenant));
                var route = api + $"/agents/{instance}/automations";
                var created = await owner.PostAsJsonAsync(route, new { enabled = true, name = "Reopen exact schedule", instructions = "Finish the retained occurrence.", schedule = new { kind = "oneShot", timeZone = "UTC", atUtc = clock.GetUtcNow().AddMinutes(30).ToString("O") }, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1, requiresTools = true });
                Assert.Equal(HttpStatusCode.Created, created.StatusCode); automation = (await Json(created)).GetProperty("automationId").GetGuid();
                if (mode == "manual")
                {
                    var admitted = await owner.PostAsJsonAsync(route + $"/{automation}/run", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1, expectedAutomationRevision = 1 });
                    Assert.True(admitted.StatusCode == HttpStatusCode.Created, await admitted.Content.ReadAsStringAsync()); runId = (await Json(admitted)).GetProperty("runId").GetGuid();
                }
                else
                {
                    clock.Advance(TimeSpan.FromMinutes(31)); await first.Services.GetRequiredService<TenantAutomationService>().AdmitDueAsync();
                    runId = Assert.Single((await first.Services.GetRequiredService<ITenantRunStore>().ListTenantSessionRunsAsync(tenant, null, null, 0, 100, instanceId: instance)).Items).AgentRunId;
                }
                var run = (await first.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(tenant, runId))!;
                Assert.Equal(AgentRunStatus.Queued, run.Status);
                if (mode == "archived")
                {
                    var response = await owner.PutAsJsonAsync(api + $"/agents/{instance}/lifecycle", new { lifecycle = 1, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 });
                    Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
                }
                await using var db = first.Services.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>().CreateDbContext();
                foreach (var sql in new[] {
                    $"UPDATE Automations SET ProfileId = '{Guid.NewGuid():D}' WHERE AutomationId = '{automation:D}'",
                    $"UPDATE TriggerOccurrences SET EvidenceJson = '{{}}' WHERE AcceptedAgentRunId = '{runId:D}'",
                    $"UPDATE TenantSessions SET CreatedByPrincipalId = '{human:D}' WHERE SessionId = '{run.SessionId:D}'" })
                    Assert.Equal(19, (await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync(sql.Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal)))).SqliteErrorCode);
                await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync("20261020170000_TenantAccountOperationRead"));
                Assert.Contains("20261020180000_TenantAutonomousAutomation", await db.Database.GetAppliedMigrationsAsync());
            }
            var model = new OwnedAutomationModel();
            await using (var second = new OAuthFactory(root, sqlite: true, preserve: true, execute: true, model: model, servicesOverride: services => { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock); }))
            {
                using var owner = ConnectedClient(second); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
                var runs = second.Services.GetRequiredService<ITenantRunStore>(); AgentRun run;
                if (mode == "archived")
                {
                    Assert.Equal(0, await second.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8));
                    Assert.Equal(AgentRunStatus.Queued, (await runs.FindTenantRunAsync(tenant, runId))!.Status); Assert.Empty(model.Requests);
                    var restored = await owner.PutAsJsonAsync($"/api/v2/tenants/{tenant}/agents/{instance}/lifecycle", new { lifecycle = 0, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 2 });
                    Assert.True(restored.IsSuccessStatusCode, await restored.Content.ReadAsStringAsync());
                }
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while (!(run = (await runs.FindTenantRunAsync(tenant, runId, deadline.Token))!).IsTerminal)
                { await second.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8, deadline.Token); await Task.Delay(25, deadline.Token); }
                Assert.Equal(AgentRunStatus.Completed, run.Status);
                Assert.Equal(mode == "manual" ? human : (Guid?)null, run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId);
                Assert.Null(run.Owner.ProfileIdOrNull);
                Assert.NotEmpty(model.Requests);
                Assert.Equal(0, await second.Services.GetRequiredService<TenantAutomationService>().AdmitDueAsync());
                Assert.Single((await runs.ListTenantSessionRunsAsync(tenant, null, null, 0, 100, instanceId: instance)).Items);
                var occurrence = await second.Services.GetRequiredService<ITenantAutomationStore>().FindTenantOccurrenceAsync(tenant, run.Admission.Activation.TriggerOccurrenceId!.Value);
                Assert.Equal(runId, occurrence!.AcceptedAgentRunId); Assert.Equal(0, second.Outbound);
            }
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
