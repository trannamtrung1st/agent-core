using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Api.Tests;

public sealed partial class TenantCredentialAccessTests
{
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public Task Automation_admission_recreates_a_SQLite_host_before_work_exists_and_executes_only_the_exact_approved_occurrence(bool scheduled, bool approvedBeforeRestart) =>
        AutomationAdmissionRecoveryWorkflow(scheduled, approvedBeforeRestart);

    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public Task Pinned_Space_Environment_admission_recovers_exact_consent_once_across_SQLite_host_restart(bool scheduled, bool approvedBeforeRestart) =>
        AutomationAdmissionRecoveryWorkflow(scheduled, approvedBeforeRestart, pinned: true);

    private async Task AutomationAdmissionRecoveryWorkflow(bool scheduled, bool approvedBeforeRestart, bool pinned = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-admission-recovery-" + Guid.NewGuid().ToString("N"));
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow); Guid tenant = default, instance = default, requestId = default, runId = default, human = default;
        Guid? contextSpace = null, contextProfile = null;
        void Configure(IServiceCollection services) { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock); }
        try
        {
            await using (var first = new OAuthFactory(root, sqlite: true, preserve: true, servicesOverride: Configure))
            {
                using var owner = ConnectedClient(first); await first.Services.GetServices<IHostedService>().OfType<AgentCore.Api.AgentRunHostedService>().Single().StopAsync(CancellationToken.None);
                await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
                tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
                human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
                instance = (await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 35, expectedRevision = await Revision(owner, tenant) }))).GetProperty("instance").GetProperty("instanceId").GetGuid();
                var agent = (await first.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!; var access = first.Services.GetRequiredService<TenantAccessService>();
                var role = await access.SaveRoleAsync(human, tenant, null, "Exact admission recovery", ["automations.run", "sessions.create", "agent-runs.create"], await Revision(owner, tenant));
                await access.AssignRoleAsync(human, tenant, agent.PrincipalId, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
                if (pinned)
                {
                    var spaces = first.Services.GetRequiredService<TenantSpaceService>();
                    var shared = await spaces.CreateAsync(human, tenant, Guid.NewGuid(), "Restart context", "", "Exact retained workroom", await Revision(owner, tenant));
                    await spaces.SetMemberAsync(human, tenant, shared.SpaceId, instance, "Worker", "Captured occurrence", [], [], await Revision(owner, tenant), shared.Revision, 0);
                    shared = await spaces.ReadAsync(human, tenant, shared.SpaceId);
                    shared = await spaces.UpdateAsync(human, tenant, shared.SpaceId, shared.Name, "", shared.Instructions, true, await Revision(owner, tenant), shared.Revision);
                    contextSpace = shared.SpaceId;
                    contextProfile = (await first.Services.GetRequiredService<TenantEnvironmentService>().SaveAsync(human, tenant, shared.SpaceId, Guid.NewGuid(), "Restart profile", "", await Revision(owner, tenant), shared.Revision, 0)).EnvironmentId;
                    var contextRole = await access.SaveRoleAsync(human, tenant, null, "Retained exact context", ["spaces.read", "environments.read", "environments.activate"], await Revision(owner, tenant));
                    await access.AssignRoleAsync(human, tenant, agent.PrincipalId, contextRole.RoleId, TenantSpaceService.Scope(tenant, shared.SpaceId), await Revision(owner, tenant));
                }
                else await GrantAutomationPrivateContext(first, owner, tenant, human, agent);
                Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/agent-runs.create", new { mode = "always", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
                var route = api + $"/agents/{instance}/automations";
                var created = await owner.PostAsJsonAsync(route, new { enabled = true, name = "Retained admission consent", pinnedSpaceId = contextSpace, pinnedEnvironmentId = contextProfile, instructions = "Finish only this approved occurrence.", schedule = new { kind = "oneShot", timeZone = "UTC", atUtc = clock.GetUtcNow().AddMinutes(30).ToString("O") }, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1, requiresTools = true });
                Assert.Equal(HttpStatusCode.Created, created.StatusCode); var automation = (await Json(created)).GetProperty("automationId").GetGuid();
                if (scheduled) { clock.Advance(TimeSpan.FromMinutes(31)); await first.Services.GetRequiredService<TenantAutomationService>().AdmitDueAsync(); }
                else Assert.Equal(HttpStatusCode.Accepted, (await owner.PostAsJsonAsync(route + $"/{automation}/run", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1, expectedAutomationRevision = 1 })).StatusCode);
                requestId = Assert.Single((await Json(await owner.GetAsync(api + "/approvals/"))).GetProperty("items").EnumerateArray()).GetProperty("requestId").GetGuid();
                var request = (await first.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, requestId))!; runId = request.RunId;
                if (pinned)
                {
                    Assert.Equal(contextSpace, request.Operation.SpaceContext!.SpaceId); Assert.Equal(contextProfile, request.Operation.SpaceContext.EnvironmentId);
                    Assert.Equal(contextSpace, request.Operation.AutomationAdmission!.Current.PinnedSpaceId);
                    Assert.Equal(request.Operation.ActionHash, TenantApprovalRules.Rebuild(request.Operation).ActionHash);
                }
                if (approvedBeforeRestart)
                {
                    var decided = await owner.PostAsJsonAsync(api + $"/approvals/{requestId}/decision", new { decision = "approve", actionHash = request.Operation.ActionHash, expectedRevision = await Revision(owner, tenant), expectedRequestRevision = 1 });
                    Assert.True(decided.IsSuccessStatusCode, await decided.Content.ReadAsStringAsync());
                }
                Assert.Null(await first.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(tenant, runId));
            }
            clock.Advance(TimeSpan.FromMinutes(1)); var model = new OwnedAutomationModel();
            await using (var second = new OAuthFactory(root, sqlite: true, preserve: true, execute: true, model: model, servicesOverride: Configure))
            {
                using var owner = ConnectedClient(second); await Login(owner, "owner@example.test", "explicit-synthetic-test-password"); var api = $"/api/v2/tenants/{tenant}";
                if (!approvedBeforeRestart)
                {
                    var detail = await Json(await owner.GetAsync(api + $"/approvals/{requestId}"));
                    Assert.Equal(JsonValueKind.Null, detail.GetProperty("runId").ValueKind); Assert.Empty(model.Requests);
                    Assert.Null(await second.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(tenant, runId));
                    var decided = await owner.PostAsJsonAsync(api + $"/approvals/{requestId}/decision", new { decision = "approve", actionHash = detail.GetProperty("preparedAction").GetProperty("actionHash").GetString(), expectedRevision = await Revision(owner, tenant), expectedRequestRevision = 1 });
                    Assert.True(decided.IsSuccessStatusCode, await decided.Content.ReadAsStringAsync());
                }
                var runs = second.Services.GetRequiredService<ITenantRunStore>(); AgentRun? run = null;
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while (run?.IsTerminal != true)
                {
                    await second.Services.GetRequiredService<TenantApprovalService>().DispatchPendingAsync(deadline.Token);
                    await second.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8, deadline.Token);
                    run = await runs.FindTenantRunAsync(tenant, runId, deadline.Token); await Task.Delay(25, deadline.Token);
                }
                Assert.Equal(AgentRunStatus.Completed, run.Status);
                if (pinned)
                {
                    Assert.Equal(contextSpace, run.Admission.Configuration!.SpaceContext!.SpaceId); Assert.Equal(contextProfile, run.Admission.Configuration.SpaceContext.EnvironmentId);
                    var savedSession = (await second.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(tenant, run.SessionId))!.Snapshot;
                    Assert.True(run.Admission.Configuration.SpaceContext.MatchesSelection(savedSession.ContextSelection));
                    var occurrence = (await second.Services.GetRequiredService<ITenantAutomationStore>().FindTenantOccurrenceAsync(tenant, run.Admission.Activation.TriggerOccurrenceId!.Value))!;
                    Assert.Equal(contextSpace, occurrence.PinnedSpaceId); Assert.Equal(contextProfile, occurrence.PinnedEnvironmentId);
                } Assert.Equal(scheduled ? (Guid?)null : human, run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId);
                Assert.Single((await runs.ListTenantSessionRunsAsync(tenant, null, null, 0, 100, instanceId: instance)).Items); Assert.NotEmpty(model.Requests);
                Assert.Equal(OccurrenceRoutingDisposition.AcceptedDurable, (await second.Services.GetRequiredService<ITenantAutomationStore>().FindTenantOccurrenceAsync(tenant, run.Admission.Activation.TriggerOccurrenceId!.Value))!.Disposition);
                Assert.Equal(0, await second.Services.GetRequiredService<TenantApprovalService>().DispatchPendingAsync()); Assert.Equal(0, second.Outbound);
            }
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
