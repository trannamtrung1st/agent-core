using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Hosting;

namespace AgentCore.Api.Tests;

public sealed partial class TenantCredentialAccessTests
{
    public static TheoryData<bool, string> AutomationConsentCases
    {
        get
        {
            var cases = new TheoryData<bool, string>();
            foreach (var sqlite in new[] { false, true })
                foreach (var mode in new[] { "manual", "scheduled", "reject", "cancel", "expired", "grant-run", "grant-session", "grant-automation", "secondary-revoked", "transaction-revoked", "agent-archived", "registration-cancelled", "wrong-hash", "two-missing", "no-independent-reviewer", "scoped-reader", "daily", "weekly", "interval" }) cases.Add(sqlite, mode);
            return cases;
        }
    }

    [Theory] [MemberData(nameof(AutomationConsentCases))]
    public async Task Automation_admission_consent_reserves_only_the_exact_occurrence_before_any_work_exists(bool sqlite, string mode)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = new OAuthFactory(sqlite: sqlite, servicesOverride: services => { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock); });
        using var owner = ConnectedClient(factory);
        await factory.Services.GetServices<IHostedService>().OfType<AgentCore.Api.AgentRunHostedService>().Single().StopAsync(CancellationToken.None); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var api = $"/api/v2/tenants/{tenant}"; var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
        var instance = (await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 32, expectedRevision = await Revision(owner, tenant) }))).GetProperty("instance").GetProperty("instanceId").GetGuid();
        var agent = (await factory.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!;
        var access = factory.Services.GetRequiredService<TenantAccessService>();
        var missing = mode switch { "grant-run" => "agent-runs.create", "grant-session" => "sessions.create", "grant-automation" => "automations.run", _ => "" };
        var permissions = new[] { "automations.run", "sessions.create", "agent-runs.create" }.Where(p => p != missing && (mode != "two-missing" || p == "automations.run")).ToArray();
        var role = await access.SaveRoleAsync(human, tenant, null, "Admission executor", permissions, await Revision(owner, tenant));
        await access.AssignRoleAsync(human, tenant, agent.PrincipalId, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/agent-runs.create", new { mode = "always", independentReview = mode == "no-independent-reviewer", expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
        var route = api + $"/agents/{instance}/automations";
        object timing = mode switch {
            "daily" => new { kind = "daily", timeZone = "UTC", interval = 1, localTime = "12:00", maxOccurrences = 2 },
            "weekly" => new { kind = "weekly", timeZone = "UTC", interval = 1, localTime = "12:00", weekdays = new[] { 1, 5 }, maxOccurrences = 2 },
            "interval" => new { kind = "fixedInterval", timeZone = "UTC", interval = 3600, anchorAtUtc = clock.GetUtcNow().AddMinutes(30).ToString("O"), maxOccurrences = 2 },
            _ => new { kind = "oneShot", timeZone = "UTC", atUtc = clock.GetUtcNow().AddMinutes(30).ToString("O") } };
        var created = await owner.PostAsJsonAsync(route, new { enabled = true, name = "Exact admission task", instructions = "Finish only the saved occurrence.", schedule = timing,
            expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = agent.Instance.Revision, requiresTools = true });
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var automation = (await Json(created)).GetProperty("automationId").GetGuid();
        var automationStore = factory.Services.GetRequiredService<ITenantAutomationStore>();
        var approvals = factory.Services.GetRequiredService<ITenantApprovalStore>();
        var runs = factory.Services.GetRequiredService<ITenantRunStore>();
        async Task NoWork() => Assert.Empty((await runs.ListTenantSessionRunsAsync(tenant, null, null, 0, 100, instanceId: instance)).Items);
        Guid occurrence, requestId;
        if (mode == "scheduled")
        {
            clock.Advance(TimeSpan.FromMinutes(31)); await factory.Services.GetRequiredService<TenantAutomationService>().AdmitDueAsync();
            var page = await Json(await owner.GetAsync(api + "/approvals/")); requestId = Assert.Single(page.GetProperty("items").EnumerateArray()).GetProperty("requestId").GetGuid();
            occurrence = (await approvals.FindApprovalAsync(tenant, requestId))!.Operation.AutomationAdmission!.Occurrence.OccurrenceId;
        }
        else
        {
            var result = await owner.PostAsJsonAsync(route + $"/{automation}/run", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = agent.Instance.Revision, expectedAutomationRevision = 1 });
            if (mode is "two-missing" or "no-independent-reviewer") { Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode); await NoWork(); Assert.Equal(0, (await Json(await owner.GetAsync(api + "/approvals/"))).GetProperty("total").GetInt32()); return; }
            Assert.True(result.StatusCode == HttpStatusCode.Accepted, await result.Content.ReadAsStringAsync());
            var body = await Json(result); Assert.Equal(JsonValueKind.Null, body.GetProperty("runId").ValueKind); Assert.Equal(JsonValueKind.Null, body.GetProperty("sessionId").ValueKind);
            occurrence = body.GetProperty("occurrenceId").GetGuid(); requestId = body.GetProperty("approvalRequestId").GetGuid();
        }
        await NoWork();
        var request = (await approvals.FindApprovalAsync(tenant, requestId))!;
        Assert.Equal(mode == "scheduled" ? (Guid?)null : human, request.OriginalInitiatorPrincipalId);
        Assert.Equal(missing.Length == 0 ? ApprovalKind.Review : ApprovalKind.ExceptionalGrant, request.Kind);
        var decoded = JsonSerializer.Deserialize<TenantApprovalRequest>(JsonSerializer.Serialize(request))!;
        Assert.True(TenantAutomationRules.Same(request.Operation.AutomationAdmission!.Current, decoded.Operation.AutomationAdmission!.Current));
        Assert.Equal(request.Operation.ActionHash, TenantApprovalRules.Rebuild(decoded.Operation).ActionHash); Assert.Equal(request.Operation, decoded.Operation);
        Assert.Null(await runs.FindTenantRunAsync(tenant, request.RunId));
        var pending = (await automationStore.FindTenantOccurrenceAsync(tenant, occurrence))!;
        Assert.Equal(OccurrenceRoutingDisposition.AwaitingDurableWork, pending.Disposition); Assert.Null(pending.AcceptedAgentRunId);
        var saved = (await automationStore.FindTenantAutomationAsync(tenant, automation))!;
        var duplicate = await owner.PostAsJsonAsync(route + $"/{automation}/run", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = agent.Instance.Revision, expectedAutomationRevision = saved.Revision });
        Assert.True(duplicate.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.Forbidden, await duplicate.Content.ReadAsStringAsync()); await NoWork();
        var detail = await Json(await owner.GetAsync(api + $"/approvals/{requestId}"));
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("runId").ValueKind); Assert.Equal(JsonValueKind.Null, detail.GetProperty("sessionId").ValueKind);
        Assert.Equal("admit_automation_occurrence", detail.GetProperty("preparedAction").GetProperty("operation").GetString());
        Assert.True(detail.GetProperty("review").GetProperty("canDecide").GetBoolean());
        if (sqlite)
        {
            await using var db = factory.Services.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>().CreateDbContext();
            Assert.False(db.Database.HasPendingModelChanges());
            foreach (var sql in new[] { $"UPDATE TenantApprovalRequests SET AutomationId = '{Guid.NewGuid():D}' WHERE lower(RequestId) = '{requestId:D}'",
                $"UPDATE TriggerOccurrences SET Disposition=6 WHERE OccurrenceId='{occurrence:D}'",
                $"UPDATE TriggerOccurrences SET EvidenceJson='{{}}' WHERE OccurrenceId='{occurrence:D}'" })
                await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync(sql.Replace("{", "{{").Replace("}", "}}")));
        }
        if (mode == "scoped-reader")
        {
            var identity = factory.Services.GetRequiredService<IdentityService>(); var account = await identity.RegisterAsync("admission-reader@example.test", "Exact admission reader", "strong-admission-reader-password");
            var readRole = await access.SaveRoleAsync(human, tenant, null, "Review metadata without Automation access", ["approvals.read", "agents.read", "automations.run", "sessions.create", "agent-runs.create"], await Revision(owner, tenant));
            var invitation = await access.InviteAsync(human, tenant, account.LoginKey, readRole.RoleId, await Revision(owner, tenant));
            var member = await access.AcceptInvitationAsync(account.AccountId, tenant, invitation.Invitation.InvitationId, invitation.Token);
            using var reader = ConnectedClient(factory); await Login(reader, "admission-reader@example.test", "strong-admission-reader-password");
            Assert.Equal(0, (await Json(await reader.GetAsync(api + "/approvals/?page=0&pageSize=1"))).GetProperty("total").GetInt32());
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(api + $"/approvals/{requestId}")).StatusCode);
            var exactRead = await access.SaveRoleAsync(human, tenant, null, "Read this exact source", ["automations.read"], await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, member.PrincipalId, exactRead.RoleId, TenantAutomationRules.Scope(tenant, automation), await Revision(owner, tenant));
            Assert.Equal(1, (await Json(await reader.GetAsync(api + "/approvals/?page=0&pageSize=1"))).GetProperty("total").GetInt32());
            var visible = await Json(await reader.GetAsync(api + $"/approvals/{requestId}")); Assert.False(visible.GetProperty("review").GetProperty("canDecide").GetBoolean());
        }
        var service = factory.Services.GetRequiredService<TenantApprovalService>();
        TenantAutomationAdmission? captured = null; ApprovalProcessedSignal? capturedSignal = null;
        if (mode == "expired") clock.Advance(TimeSpan.FromHours(2));
        else
        {
            if (mode == "wrong-hash")
            {
                Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync(api + $"/approvals/{requestId}/decision", new { decision = "approve", actionHash = new string('0', 64), expectedRevision = await Revision(owner, tenant), expectedRequestRevision = 1 })).StatusCode);
                await NoWork();
            }
            var witness = new { decision = mode == "reject" ? "reject" : "approve", actionHash = request.Operation.ActionHash, expectedRevision = await Revision(owner, tenant), expectedRequestRevision = 1 };
            var response = await owner.PostAsJsonAsync(api + $"/approvals/{requestId}/" + (mode == "cancel" ? "cancel" : "decision"), witness);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            if (mode == "transaction-revoked")
            {
                var approved = (await approvals.FindApprovalAsync(tenant, requestId))!;
                var target = await factory.Services.GetRequiredService<TenantAutomationAdmissionTarget>().RequireAsync(approved, CancellationToken.None);
                captured = TenantAutomationAdmissionApprovalRules.Build(approved, target.Agent, target.Configuration, clock.GetUtcNow());
                capturedSignal = Assert.Single(await approvals.PendingApprovalSignalsAsync(100));
            }
            if (mode == "agent-archived") Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + $"/agents/{instance}/lifecycle", new { lifecycle = 1, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = agent.Instance.Revision })).StatusCode);
            if (mode is "secondary-revoked" or "transaction-revoked") await access.SaveRoleAsync(human, tenant, role.RoleId, role.Name, ["automations.run", "agent-runs.create"], await Revision(owner, tenant));
            if (mode == "registration-cancelled") Assert.Equal(HttpStatusCode.OK, (await owner.DeleteAsync(route + $"/{automation}?expectedRevision={await Revision(owner, tenant)}&expectedInstanceRevision={agent.Instance.Revision}&expectedAutomationRevision={saved.Revision}")).StatusCode);
        }
        clock.Advance(TimeSpan.FromMinutes(1));
        if (captured is not null) Assert.True(await automationStore.TrySettleTenantAutomationApprovalAsync(await Revision(owner, tenant), capturedSignal!, captured, null, Guid.NewGuid(), clock.GetUtcNow()));
        await service.DispatchPendingAsync();
        var settled = (await automationStore.FindTenantOccurrenceAsync(tenant, occurrence))!;
        var refused = mode is "reject" or "cancel" or "expired" or "secondary-revoked" or "transaction-revoked" or "agent-archived" or "registration-cancelled";
        Assert.Equal(refused ? OccurrenceRoutingDisposition.Rejected : OccurrenceRoutingDisposition.AcceptedDurable, settled.Disposition);
        if (refused) { await NoWork(); Assert.Null(await runs.FindTenantRunAsync(tenant, request.RunId)); }
        else
        {
            var run = Assert.Single((await runs.ListTenantSessionRunsAsync(tenant, null, null, 0, 100, instanceId: instance)).Items);
            Assert.Equal(request.RunId, run.AgentRunId); Assert.Equal(request.SessionId, run.SessionId); Assert.True(run.CreatedAtUtc > request.CreatedAt);
            Assert.Equal(request.OriginalInitiatorPrincipalId, run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId);
            Assert.Equal(request.RootActivationId, run.Admission.ExecutionContext.RootActivationId); Assert.Null(run.Admission.ExecutionContext.ParentRunId);
            Assert.Equal(0, await service.DispatchPendingAsync());
        }
        var grant = await approvals.FindOperationGrantAsync(tenant, requestId);
        if (missing.Length != 0) Assert.Equal(OperationGrantStatus.Consumed, grant!.Status); else Assert.Null(grant);
        detail = await Json(await owner.GetAsync(api + $"/approvals/{requestId}"));
        Assert.Equal(refused ? "notAdmitted" : "admitted", detail.GetProperty("review").GetProperty("executionState").GetString());
        Assert.Equal(0, factory.Outbound);
    }
}
