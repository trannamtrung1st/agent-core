using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Api.Tests;

public sealed partial class TenantCredentialAccessTests
{
    private static async Task<(Guid Tenant, Guid Instance, Guid Human)> AuthoringAgent(OAuthFactory factory, HttpClient owner)
    {
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var api = $"/api/v2/tenants/{tenant}";
        var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
        var instance = (await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 36, expectedRevision = await Revision(owner, tenant) }))).GetProperty("instance").GetProperty("instanceId").GetGuid();
        await TenantSessionAccessTests.GrantNativePrivateSpaceRead(owner, tenant, instance);
        return (tenant, instance, human);
    }
    private static JsonNode AuthoringSchedule(string kind, DateTimeOffset now)
    {
        var future = now.AddHours(1);
        return kind switch {
            "once" => JsonSerializer.SerializeToNode(new { kind = "oneShot", atUtc = future.ToString("O"), timeZone = "UTC" })!,
            "fixed" => JsonSerializer.SerializeToNode(new { kind = "fixedInterval", interval = 60, anchorAtUtc = now.ToString("O"), maxOccurrences = 100 })!,
            "daily" => JsonSerializer.SerializeToNode(new { kind = "daily", interval = 1, localTime = future.ToString("HH:mm"), timeZone = "UTC" })!,
            "weekly" => JsonSerializer.SerializeToNode(new { kind = "weekly", interval = 1, localTime = future.ToString("HH:mm"), weekdays = new[] { (int)future.DayOfWeek }, timeZone = "UTC" })!,
            _ => throw new ArgumentException(kind)
        };
    }
    [Theory(Timeout = 120000)]
    [InlineData(false, "once")] [InlineData(true, "once")] [InlineData(false, "fixed")] [InlineData(true, "fixed")]
    [InlineData(false, "daily")] [InlineData(true, "daily")] [InlineData(false, "weekly")] [InlineData(true, "weekly")]
    public async Task Tenant_automation_authoring_preserves_due_occurrence_through_real_edit_reopen_admission_and_completion(bool sqlite, string kind)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow); var model = new OwnedAutomationModel();
        await using var factory = new OAuthFactory(sqlite: sqlite, execute: true, model: model, servicesOverride: services => {
            services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock);
        });
        using var owner = ConnectedClient(factory); var (tenant, instance, human) = await AuthoringAgent(factory, owner);
        var route = $"/api/v2/tenants/{tenant}/agents/{instance}/automations";
        var body = JsonSerializer.SerializeToNode(new { enabled = true, name = "Original schedule", instructions = "Original task.", requiresTools = true,
            expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 })!.AsObject();
        body["schedule"] = AuthoringSchedule(kind, clock.GetUtcNow());
        var saved = await Json(await owner.PostAsJsonAsync(route, body)); var id = saved.GetProperty("automationId").GetGuid();
        var store = factory.Services.GetRequiredService<ITenantAutomationStore>(); var original = (await store.FindTenantAutomationAsync(tenant, id))!;
        clock.Advance(original.NextOccurrenceAtUtc!.Value - clock.GetUtcNow() + TimeSpan.FromSeconds(5));
        body["name"] = "Renamed due schedule"; body["instructions"] = "Finish the revised task without skipping its selected occurrence.";
        body["expectedRevision"] = await Revision(owner, tenant); body["expectedAutomationRevision"] = original.Revision;
        var updated = await Json(await owner.PutAsJsonAsync(route + $"/{id}", body));
        Assert.Equal(original.NextOccurrenceAtUtc, updated.GetProperty("nextOccurrenceAtUtc").GetDateTimeOffset());
        var retained = (await store.FindTenantAutomationAsync(tenant, id))!;
        Assert.Equal(original.TriggerRevision, retained.TriggerRevision); Assert.Equal(original.Triggers[0].TriggerId, retained.Triggers[0].TriggerId);
        Assert.Equal(0, retained.OccurrenceCount); Assert.Equal(original.Provenance.CreatedAt, retained.Provenance.CreatedAt);
        if (sqlite)
        {
            var reopened = new SqliteIamStore(factory.Services.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>());
            Assert.True(TenantAutomationRules.Same(retained, (await reopened.FindTenantAutomationAsync(tenant, id))!));
        }
        // There was deliberately no execution permission while the due edit was made.
        // Supply ordinary current rights only after the exact retained state is checked.
        var agent = (await factory.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!;
        var access = factory.Services.GetRequiredService<TenantAccessService>();
        var role = await access.SaveRoleAsync(human, tenant, null, "Execute retained due work", ["automations.run", "sessions.create", "agent-runs.create"], await Revision(owner, tenant));
        await access.AssignRoleAsync(human, tenant, agent.PrincipalId, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
        await factory.Services.GetRequiredService<TenantAutomationService>().AdmitDueAsync();
        var runs = factory.Services.GetRequiredService<ITenantRunStore>();
        var run = Assert.Single((await runs.ListTenantSessionRunsAsync(tenant, null, null, 0, 100, instanceId: instance)).Items);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!run.IsTerminal)
        {
            await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8, deadline.Token);
            await Task.Delay(25, deadline.Token); run = (await runs.FindTenantRunAsync(tenant, run.AgentRunId, deadline.Token))!;
        }
        Assert.True(run.Status == AgentRunStatus.Completed, $"{run.Status}: {run.Failure?.Code}");
        Assert.Equal("Finish the revised task without skipping its selected occurrence.", run.Admission.TaskIntent!.Text);
        Assert.Equal(id, TenantAutomationRules.SourceAutomation(run)); Assert.Null(run.Owner.ProfileIdOrNull);
        Assert.Equal(0, await factory.Services.GetRequiredService<TenantAutomationService>().AdmitDueAsync());
        Assert.Single((await runs.ListTenantSessionRunsAsync(tenant, null, null, 0, 100, instanceId: instance)).Items);
        Assert.Equal(0, factory.Outbound);
    }

    [Theory] [InlineData(false, "skip")] [InlineData(true, "skip")] [InlineData(false, "count")] [InlineData(true, "count")]
    [InlineData(false, "suspended")] [InlineData(true, "suspended")] [InlineData(false, "cancel")] [InlineData(true, "cancel")]
    public async Task Tenant_automation_authoring_transaction_rejects_forged_progress_without_any_partial_effect(bool sqlite, string mode)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = new OAuthFactory(sqlite: sqlite, servicesOverride: services => { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock); });
        using var owner = ConnectedClient(factory); var (tenant, instance, human) = await AuthoringAgent(factory, owner);
        var route = $"/api/v2/tenants/{tenant}/agents/{instance}/automations";
        var body = JsonSerializer.SerializeToNode(new { enabled = true, name = "Retained schedule", instructions = "Keep exact progress.", expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 })!.AsObject();
        body["schedule"] = AuthoringSchedule("fixed", clock.GetUtcNow());
        var id = (await Json(await owner.PostAsJsonAsync(route, body))).GetProperty("automationId").GetGuid();
        var store = factory.Services.GetRequiredService<ITenantAutomationStore>(); var current = (await store.FindTenantAutomationAsync(tenant, id))!;
        var identity = factory.Services.GetRequiredService<IdentityService>(); var login = (await identity.SignInAsync("owner@example.test", "explicit-synthetic-test-password"))!;
        var security = factory.Services.GetRequiredService<IIamStore>(); var state = (await security.ReadTenantAsync(tenant))!; var at = clock.GetUtcNow();
        var proposed = new Automation(id, current.Owner, mode == "suspended" ? AutomationStatus.SuspendedPolicy : mode == "cancel" ? AutomationStatus.Cancelled : current.Status,
            current.Instructions, current.Triggers, mode == "skip" ? at.AddHours(5) : current.NextOccurrenceAtUtc, current.ExpiresAtUtc, mode == "count" ? 10 : current.OccurrenceCount,
            current.Revision + 1, current.TriggerRevision, current.Provenance.WithUpdated(at), null);
        await Assert.ThrowsAsync<ArgumentException>(() => store.TrySaveTenantAutomationAsync(state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } }, state.Tenant.Revision,
            proposed, current.Revision, 1, new(login.Account.AccountId, login.Session.SessionId, human), "automations.update",
            new(Guid.NewGuid(), tenant, human, "automations.update", id.ToString("D"), "allowed", "automation_saved", at)).AsTask());
        Assert.Equal(state.Tenant.Revision, (await security.ReadTenantAsync(tenant))!.Tenant.Revision);
        Assert.True(TenantAutomationRules.Same(current, (await store.FindTenantAutomationAsync(tenant, id))!));
        Assert.Empty((await factory.Services.GetRequiredService<ITenantRunStore>().ListTenantSessionRunsAsync(tenant, null, null, 0, 100, instanceId: instance)).Items);
    }
}
