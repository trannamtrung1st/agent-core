using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Api.Security;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Api.Tests;

public sealed partial class TenantCredentialAccessTests
{
    private sealed class OwnedAutomationModel : ILanguageModel
    {
        public readonly ConcurrentQueue<ModelRequest> Requests = new();
        public ModelCapabilities Capabilities => new AgentCore.Infrastructure.Providers.Synthetic.ScriptedLanguageModel().Capabilities;
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Enqueue(request); ct.ThrowIfCancellationRequested(); await Task.Yield();
            Assert.Contains(request.Tools ?? [], t => t.Name == ToolCatalog.WorkComplete);
            yield return new ModelToolCallEvent(new("owned-auto-complete", ToolCatalog.WorkComplete, """{"outcome":"Response","summary":"Owned automation completed.","attentionRequired":false}"""));
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
        }
    }
    public static TheoryData<bool, string> AutomationOwnerCases
    {
        get
        {
            var cases = new TheoryData<bool, string>();
            foreach (var sqlite in new[] { false, true })
            foreach (var mode in new[] { "manual", "scheduled", "disabled", "cancelled", "stale", "missing-executor", "foreign-route", "review-required", "policy-change", "archived", "archived-restored", "dst-weekly", "scoped-human", "requester-policy", "technical-close", "due-cursor", "actual-invoker" }) cases.Add(sqlite, mode);
            return cases;
        }
    }
    [Theory] [MemberData(nameof(AutomationOwnerCases))]
    public async Task Tenant_automations_use_current_human_scope_owned_rows_fresh_occurrence_roots_and_actual_worker_outcomes(bool sqlite, string mode)
    {
        var clock = new FakeTimeProvider(mode == "dst-weekly" ? DateTimeOffset.Parse("2027-03-22T08:30:00Z") : DateTimeOffset.UtcNow); var model = new OwnedAutomationModel();
        var application = typeof(TenantAutomationService).Assembly;
        foreach (var retired in new[] { "Triggers.AutomationService", "Ports.IAutomationService", "Triggers.TriggerScheduler", "Triggers.TriggerAdmissionGuard",
            "Triggers.TriggerPolicyRecoveryService", "Triggers.TriggerInstancePolicyReconciliationService", "Triggers.TriggerOccurrenceRouter",
            "Triggers.DurableOrderEventIngress", "Events.WebhookEventService", "Events.ExternalEventIngress",
            "Execution.BackgroundOccurrenceIntake", "Execution.BackgroundCompletionReporter" })
            Assert.Null(application.GetType("AgentCore.Application." + retired));
        foreach (var retired in new[] { "SubmitOccurrenceAsync", "BeginAcceptedOccurrenceAsync", "AbandonOccurrenceReservationAsync" })
            Assert.Null(typeof(AgentCore.Application.Sessions.SessionRuntime).GetMethod(retired));

        await using var factory = new OAuthFactory(sqlite: sqlite, execute: true, model: model, servicesOverride: services => {
            services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock);
        });
        using var owner = ConnectedClient(factory); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var api = $"/api/v2/tenants/{tenant}"; var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
        var instance = (await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 35, expectedRevision = await Revision(owner, tenant) }))).GetProperty("instance").GetProperty("instanceId").GetGuid();
        var agent = (await factory.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!;
        var access = factory.Services.GetRequiredService<TenantAccessService>();
        await GrantAutomationPrivateContext(factory, owner, tenant, human, agent);
        if (mode is not ("missing-executor" or "due-cursor"))
        {
            var role = await access.SaveRoleAsync(human, tenant, null, "Execute owned Automations", ["automations.run", "sessions.create", "agent-runs.create"], await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, agent.PrincipalId, role.RoleId, AgentCore.Domain.Security.AccessScope.ForTenant(tenant), await Revision(owner, tenant));
        }
        if (mode == "review-required")
        {
            var response = await owner.PutAsJsonAsync(api + "/operation-policies/agent-runs.create", new { mode = "always", independentReview = false,
                expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 }); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        var route = api + $"/agents/{instance}/automations";
        var at = clock.GetUtcNow().AddMinutes(30);
        object Schedule() => mode == "dst-weekly"
            ? new { kind = "weekly", timeZone = "Europe/London", interval = 1, localTime = "09:00", weekdays = new[] { 1 } }
            : new { kind = "oneShot", timeZone = "UTC", atUtc = at.ToString("O") };
        object Body(long security, long revision = 0) => new { enabled = mode != "disabled", name = "Exact owned automation", instructions = "Finish the exact owned task.",
            schedule = Schedule(), expectedRevision = security,
            expectedInstanceRevision = agent.Instance.Revision, expectedAutomationRevision = revision, requiresTools = true,
            tenantId = Guid.NewGuid(), executorPrincipalId = Guid.NewGuid() }; // Cannot supply authority through extra DTO fields.
        using var created = await owner.PostAsJsonAsync(route, Body(await Revision(owner, tenant)));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var value = await Json(created); var id = value.GetProperty("automationId").GetGuid();
        var store = factory.Services.GetRequiredService<ITenantAutomationStore>(); var saved = (await store.FindTenantAutomationAsync(tenant, id))!;
        Assert.Equal(TriggerOwner.ForTenant(tenant, instance, agent.PrincipalId), saved.Owner); Assert.Null(saved.Owner.ProfileIdOrNull);
        Assert.Null(await store.FindTenantAutomationAsync(Guid.NewGuid(), id));
        Assert.Equal(0, (await store.ListTenantAutomationsAsync(tenant, instance, [], 0, 1)).Total);
        Assert.Equal(1, (await Json(await owner.GetAsync(route))).GetProperty("total").GetInt32());
        var legacy = factory.Services.GetRequiredService<ITriggerStore>();
        Assert.Null(await legacy.GetAsync(new(instance, AgentCore.Domain.Conversation.LocalUserProfile.Id), id));
        Assert.DoesNotContain(await legacy.ListDueAsync(at.AddMinutes(1), 100), a => a.AutomationId == id);
        if (sqlite)
        {
            await using var db = factory.Services.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>().CreateDbContext();
            Assert.False(db.Database.HasPendingModelChanges()); Assert.False(await db.Automations.AnyAsync(a => a.AutomationId == id.ToString("D")));
        }
        if (mode == "stale")
        {
            using var stale = await owner.PutAsJsonAsync(route + $"/{id}", Body(await Revision(owner, tenant) - 1, saved.Revision));
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode); Assert.True(TenantAutomationRules.Same(saved, (await store.FindTenantAutomationAsync(tenant, id))!)); return;
        }
        if (mode == "foreign-route")
        {
            var other = (await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 35, expectedRevision = await Revision(owner, tenant) }))).GetProperty("instance").GetProperty("instanceId").GetGuid();
            using var hidden = await owner.GetAsync(api + $"/agents/{other}/automations/{id}"); Assert.Equal(HttpStatusCode.Forbidden, hidden.StatusCode);
            Assert.Equal(0, (await Json(await owner.GetAsync(api + $"/agents/{other}/automations"))).GetProperty("total").GetInt32()); return;
        }
        if (mode == "cancelled")
        {
            using var cancelled = await owner.DeleteAsync(route + $"/{id}?expectedRevision={await Revision(owner, tenant)}&expectedInstanceRevision={agent.Instance.Revision}&expectedAutomationRevision={saved.Revision}");
            Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode); Assert.Equal(AutomationStatus.Cancelled, (await store.FindTenantAutomationAsync(tenant, id))!.Status);
            clock.Advance(TimeSpan.FromMinutes(31)); await factory.Services.GetRequiredService<TenantAutomationService>().AdmitDueAsync();
            Assert.Empty((await factory.Services.GetRequiredService<ITenantRunStore>().ListTenantSessionRunsAsync(tenant, null, null, 0, 100, instanceId: instance)).Items); return;
        }
        if (mode == "review-required")
        {
            using var pending = await owner.PostAsJsonAsync(route + $"/{id}/run", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = agent.Instance.Revision, expectedAutomationRevision = saved.Revision });
            Assert.Equal(HttpStatusCode.Accepted, pending.StatusCode); var ack = await Json(pending);
            Assert.Equal(JsonValueKind.Null, ack.GetProperty("runId").ValueKind); Assert.Equal(JsonValueKind.Null, ack.GetProperty("sessionId").ValueKind);
            Assert.NotEqual(Guid.Empty, ack.GetProperty("approvalRequestId").GetGuid());
            Assert.Empty((await factory.Services.GetRequiredService<ITenantRunStore>().ListTenantSessionRunsAsync(tenant, null, null, 0, 100, instanceId: instance)).Items); return;
        }
        if (mode is "disabled" or "missing-executor")
        {
            using var refused = await owner.PostAsJsonAsync(route + $"/{id}/run", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = agent.Instance.Revision, expectedAutomationRevision = saved.Revision });
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Empty((await factory.Services.GetRequiredService<ITenantRunStore>().ListTenantSessionRunsAsync(tenant, null, null, 0, 100, instanceId: instance)).Items); return;
        }
        if (mode == "due-cursor")
        {
            for (var n = 0; n < 2; n++) Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync(route, Body(await Revision(owner, tenant)))).StatusCode);
            clock.Advance(TimeSpan.FromMinutes(31)); var seen = new List<Guid>(); string? cursor = null;
            for (var n = 0; n < 3; n++)
            {
                var page = await store.ListDueTenantAutomationsAsync(clock.GetUtcNow(), 1, afterId: cursor);
                var row = Assert.Single(page); Assert.Equal(tenant, row.Owner.TenantId); seen.Add(row.AutomationId); cursor = row.AutomationId.ToString("D");
            }
            Assert.Equal(3, seen.Distinct().Count()); Assert.Empty(await store.ListDueTenantAutomationsAsync(clock.GetUtcNow(), 1, afterId: cursor));
            Assert.Equal(0, await factory.Services.GetRequiredService<TenantAutomationService>().AdmitDueAsync()); return;
        }
        if (mode == "technical-close")
        {
            var identity = factory.Services.GetRequiredService<IdentityService>(); var auth = (await identity.SignInAsync("owner@example.test", "explicit-synthetic-test-password"))!;
            var security = factory.Services.GetRequiredService<IIamStore>(); var state = (await security.ReadTenantAsync(tenant))!;
            var expiring = new Automation(id, saved.Owner, saved.Status, saved.Instructions, saved.Triggers, saved.NextOccurrenceAtUtc, clock.GetUtcNow().AddMinutes(10), saved.OccurrenceCount,
                saved.Revision + 1, saved.TriggerRevision, saved.Provenance.WithUpdated(clock.GetUtcNow()), saved.SuspensionReason, saved.ModelPreference,
                saved.RequiresVision, saved.Name, saved.ExecutionTarget, saved.CompletionDelivery, saved.RequiresTools);
            Assert.True(await store.TrySaveTenantAutomationAsync(state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } }, state.Tenant.Revision, expiring, saved.Revision, agent.Instance.Revision,
                new(auth.Account.AccountId, auth.Session.SessionId, human), "automations.update", new(Guid.NewGuid(), tenant, human, "automations.update", id.ToString("D"), "allowed", "automation_saved", clock.GetUtcNow())));
            clock.Advance(TimeSpan.FromMinutes(31)); await factory.Services.GetRequiredService<TenantAutomationService>().AdmitDueAsync();
            var closed = (await store.FindTenantAutomationAsync(tenant, id))!; Assert.Equal(AutomationStatus.Expired, closed.Status); Assert.Null(closed.NextOccurrenceAtUtc);
            Assert.Equal(0, closed.OccurrenceCount); Assert.Empty((await factory.Services.GetRequiredService<ITenantRunStore>().ListTenantSessionRunsAsync(tenant, null, null, 0, 100, instanceId: instance)).Items);
            Assert.Equal(0, await factory.Services.GetRequiredService<TenantAutomationService>().AdmitDueAsync()); return;
        }
        if (mode == "scoped-human")
        {
            var identity = factory.Services.GetRequiredService<IdentityService>();
            var account = await identity.RegisterAsync("automation-reader@example.test", "Exact automation reader", "strong-automation-reader-password");
            var role = await access.SaveRoleAsync(human, tenant, null, "See Agents only", ["agents.read"], await Revision(owner, tenant));
            var invitation = await access.InviteAsync(human, tenant, account.LoginKey, role.RoleId, await Revision(owner, tenant));
            var member = await access.AcceptInvitationAsync(account.AccountId, tenant, invitation.Invitation.InvitationId, invitation.Token);
            var exactRole = await access.SaveRoleAsync(human, tenant, null, "Read exact Automation", ["automations.read"], await Revision(owner, tenant));
            var assignment = await access.AssignRoleAsync(human, tenant, member.PrincipalId, exactRole.RoleId, TenantAutomationRules.Scope(tenant, id), await Revision(owner, tenant));
            using var reader = ConnectedClient(factory); await Login(reader, account.LoginKey, "strong-automation-reader-password");
            Assert.Equal(1, (await Json(await reader.GetAsync(route + "?pageSize=1"))).GetProperty("total").GetInt32());
            Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync(route + $"/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync(route + $"/{id}/run", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = agent.Instance.Revision, expectedAutomationRevision = saved.Revision })).StatusCode);
            await access.RevokeAssignmentAsync(human, tenant, assignment.AssignmentId, await Revision(owner, tenant));
            Assert.Equal(0, (await Json(await reader.GetAsync(route + "?pageSize=1"))).GetProperty("total").GetInt32());
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(route + $"/{id}")).StatusCode); return;
        }
        if (mode == "policy-change")
        {
            async Task Patch(bool enabled, long revision)
            {
                using var patched = await owner.PatchAsJsonAsync(api + $"/agents/{instance}/settings/triggerPolicy", new {
                    expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = revision, set = new { enabled }, clear = Array.Empty<string>() });
                Assert.True(patched.IsSuccessStatusCode, await patched.Content.ReadAsStringAsync());
            }
            await Patch(false, agent.Instance.Revision);
            var suspended = (await store.FindTenantAutomationAsync(tenant, id))!;
            Assert.Equal(AutomationStatus.SuspendedPolicy, suspended.Status); Assert.Equal(saved.Revision + 1, suspended.Revision);
            clock.Advance(TimeSpan.FromMinutes(31)); Assert.Equal(0, await factory.Services.GetRequiredService<TenantAutomationService>().AdmitDueAsync());
            await Patch(true, agent.Instance.Revision + 1);
            var restored = (await store.FindTenantAutomationAsync(tenant, id))!;
            Assert.Equal(AutomationStatus.Active, restored.Status); Assert.Equal(suspended.Revision + 1, restored.Revision); Assert.Equal(saved.Instructions, restored.Instructions); return;
        }
        if (mode is "archived" or "archived-restored")
        {
            using var archived = await owner.PutAsJsonAsync(api + $"/agents/{instance}/lifecycle", new { lifecycle = 1, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = agent.Instance.Revision });
            Assert.True(archived.IsSuccessStatusCode, await archived.Content.ReadAsStringAsync());
            clock.Advance(TimeSpan.FromMinutes(31)); Assert.Equal(0, await factory.Services.GetRequiredService<TenantAutomationService>().AdmitDueAsync());
            Assert.Empty((await factory.Services.GetRequiredService<ITenantRunStore>().ListTenantSessionRunsAsync(tenant, null, null, 0, 100, instanceId: instance)).Items);
            if (mode == "archived") return;
            using var restored = await owner.PutAsJsonAsync(api + $"/agents/{instance}/lifecycle", new { lifecycle = 0,
                expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = agent.Instance.Revision + 1 });
            Assert.True(restored.IsSuccessStatusCode, await restored.Content.ReadAsStringAsync());
            Assert.True(TenantAutomationRules.Same(saved, (await store.FindTenantAutomationAsync(tenant, id))!));
        }
        if (mode == "requester-policy")
        {
            using var policy = await owner.PutAsJsonAsync(api + "/operation-policies/agent-runs.create", new { mode = "whenRequesterLacksPermission", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 });
            Assert.True(policy.IsSuccessStatusCode, await policy.Content.ReadAsStringAsync());
        }
        HttpClient invoker = owner; Guid expectedInitiator = human; HttpClient? separateInvoker = null;
        if (mode == "actual-invoker")
        {
            var identity = factory.Services.GetRequiredService<IdentityService>();
            var account = await identity.RegisterAsync("automation-invoker@example.test", "Actual manual invoker", "strong-automation-invoker-password");
            var role = await access.SaveRoleAsync(human, tenant, null, "Invoke without Chat", ["agents.read", "automations.read", "automations.run", "sessions.create", "agent-runs.create"], await Revision(owner, tenant));
            var invitation = await access.InviteAsync(human, tenant, account.LoginKey, role.RoleId, await Revision(owner, tenant));
            var member = await access.AcceptInvitationAsync(account.AccountId, tenant, invitation.Invitation.InvitationId, invitation.Token);
            separateInvoker = ConnectedClient(factory); await Login(separateInvoker, account.LoginKey, "strong-automation-invoker-password");
            invoker = separateInvoker; expectedInitiator = member.PrincipalId;
        }
        using var invokerLifetime = separateInvoker;
        Guid runId;
        if (mode is "scheduled" or "requester-policy" or "archived-restored" or "dst-weekly")
        {
            clock.Advance(TimeSpan.FromMinutes(31)); await factory.Services.GetRequiredService<TenantAutomationService>().AdmitDueAsync();
            var all = await factory.Services.GetRequiredService<ITenantRunStore>().ListTenantSessionRunsAsync(tenant, null, null, 0, 100, instanceId: instance);
            runId = Assert.Single(all.Items).AgentRunId;
            Assert.Equal(0, await factory.Services.GetRequiredService<TenantAutomationService>().AdmitDueAsync());
        }
        else
        {
            using var admitted = await invoker.PostAsJsonAsync(route + $"/{id}/run", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = agent.Instance.Revision, expectedAutomationRevision = saved.Revision, originalInitiatorPrincipalId = human, rootActivationId = Guid.NewGuid() });
            Assert.True(admitted.StatusCode == HttpStatusCode.Created, await admitted.Content.ReadAsStringAsync()); runId = (await Json(admitted)).GetProperty("runId").GetGuid();
        }
        var runs = factory.Services.GetRequiredService<ITenantRunStore>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)); AgentRun run; var passes = 0;
        while (!(run = (await runs.FindTenantRunAsync(tenant, runId, deadline.Token))!).IsTerminal) { await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8, deadline.Token);
            if (++passes > 100) Assert.Fail($"Stalled {run.Status} {run.Failure?.Code} requests={model.Requests.Count} progress={run.Progress?.Summary} checkpoint={run.Checkpoint?.ToString()} retry={run.NextRetryAtUtc}");
            await Task.Delay(25, deadline.Token); }
        Assert.True(run.Status == AgentRunStatus.Completed, $"{run.Status}: {run.Failure?.Code}; {run.Failure?.Summary}");
        var ownContext = (await factory.Services.GetRequiredService<ITenantSpaceStore>().FindPrivateSpaceAsync(tenant, instance))!;
        Assert.Equal(ownContext.SpaceId, run.Admission.Configuration!.SpaceContext!.SpaceId);
        Assert.Null(run.Admission.Configuration.SpaceContext.EnvironmentId);
        var selectedContext = (await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(tenant, run.SessionId))!.Snapshot.ContextSelection!;
        Assert.True(run.Admission.Configuration.SpaceContext.MatchesSelection(selectedContext));
        Assert.Contains("Owned automation completed", run.Result!.Text); Assert.NotEmpty(model.Requests);
        var context = run.Admission.ExecutionContext!;
        Assert.Equal(run.ActivationId, context.RootActivationId); Assert.Null(context.ParentRunId); Assert.Null(context.ParentActivationId);
        Assert.Equal(mode is "scheduled" or "requester-policy" or "archived-restored" or "dst-weekly" ? ExecutionOrigin.ScheduledOccurrence : ExecutionOrigin.ManualInvocation, context.Origin);
        Assert.Equal(mode is "scheduled" or "requester-policy" or "archived-restored" or "dst-weekly" ? (Guid?)null : expectedInitiator, context.OriginalInitiatorPrincipalId); Assert.Null(run.Owner.ProfileIdOrNull);
        var session = (await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(tenant, run.SessionId))!;
        Assert.Equal(agent.PrincipalId, session.Owner.CreatedByPrincipalId); Assert.Equal(SessionSurface.BackgroundWork, session.Snapshot.Surfaces);
        var occurrence = (await store.FindTenantOccurrenceAsync(tenant, run.Admission.Activation.TriggerOccurrenceId!.Value))!;
        Assert.Equal(runId, occurrence.AcceptedAgentRunId); Assert.Equal(run.SessionId, occurrence.ExecutionSessionId);
        if (mode == "dst-weekly")
        {
            var next = (await store.FindTenantAutomationAsync(tenant, id))!;
            Assert.Equal(AutomationStatus.Active, next.Status); Assert.Equal(1, next.OccurrenceCount);
            Assert.Equal(DateTimeOffset.Parse("2027-03-29T08:00:00Z"), next.NextOccurrenceAtUtc);
            Assert.Equal(TimeSpan.FromHours(167), next.NextOccurrenceAtUtc - at);
        }
        Assert.Equal(0, factory.Outbound);
        var prompt = string.Join("\n", model.Requests.SelectMany(r => r.Messages).Select(m => m.Text));
        Assert.Contains(mode is "scheduled" or "requester-policy" or "archived-restored" or "dst-weekly" ? "\"requester\":null" : "\"agentChatAdmitted\":false", prompt);
        if (mode == "actual-invoker") { Assert.Contains("Actual manual invoker", prompt); Assert.NotEqual(human, context.OriginalInitiatorPrincipalId); }
    }
}
