using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
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
    public static TheoryData<bool, string> TenantEventRecoveryCases
    {
        get { var cases = new TheoryData<bool, string>(); foreach (var core in new[] { false, true }) foreach (var phase in new[] { "receipt", "bucket", "approval", "approved", "admitted" }) cases.Add(core, phase); return cases; }
    }
    [Theory] [MemberData(nameof(TenantEventRecoveryCases))]
    public async Task Tenant_event_recovers_captured_receipt_bucket_and_exact_consent_without_duplicate_work(bool core, string phase)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-event-recovery-" + Guid.NewGuid().ToString("N"));
        var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        Guid tenant = default, instance = default, automation = default, child = default, eventId = default; Guid? requestId = null, runId = null; string? hash = null;
        void Configure(IServiceCollection services) { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock); }
        async Task Stop(OAuthFactory host) { foreach (var service in host.Services.GetServices<IHostedService>().Where(s => s is AgentCore.Api.AgentRunHostedService or AgentCore.Api.TriggerSchedulerHostedService)) await service.StopAsync(CancellationToken.None); }
        async Task Decide(OAuthFactory host, HttpClient owner)
        {
            var request = (await host.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, requestId!.Value))!;
            Assert.Equal(hash, request.Operation.ActionHash);
            using var response = await owner.PostAsJsonAsync($"/api/v2/tenants/{tenant}/approvals/{requestId}/decision", new { decision = "approve", actionHash = hash, expectedRevision = await Revision(owner, tenant), expectedRequestRevision = request.Revision });
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        }
        try
        {
            await using (var first = new OAuthFactory(root, sqlite: true, preserve: true, servicesOverride: Configure))
            {
                using var owner = ConnectedClient(first); await Login(owner, "owner@example.test", "explicit-synthetic-test-password"); await Stop(first);
                tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
                var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
                instance = (await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 32, expectedRevision = await Revision(owner, tenant) }))).GetProperty("instance").GetProperty("instanceId").GetGuid();
                var agent = (await first.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!;
                var access = first.Services.GetRequiredService<TenantAccessService>();
                var role = await access.SaveRoleAsync(human, tenant, null, "Recovered event executor", ["automations.run", "sessions.create", "agent-runs.create", "webhooks.read", "core-events.read"], await Revision(owner, tenant));
                await access.AssignRoleAsync(human, tenant, agent.PrincipalId, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
                using var policy = await owner.PutAsJsonAsync(api + "/operation-policies/agent-runs.create", new { mode = "always", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 }); Assert.True(policy.IsSuccessStatusCode);
                var webhook = await Json(await owner.PostAsJsonAsync(api + "/webhooks", new { displayName = "Retained event source", eventKey = "recover.event", expectedRevision = await Revision(owner, tenant) }));
                var source = webhook.GetProperty("source").GetProperty("resourceId").GetGuid();
                var saved = await Json(await owner.PostAsJsonAsync(api + $"/agents/{instance}/automations", new { enabled = true, name = "Recover captured event", instructions = "Use only this factual source.", requiresTools = false,
                    expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1, triggers = new[] { new { kind = "event", enabled = true,
                    source = core ? new { kind = "builtin", key = (string?)"instance.config_changed", eventId = (string?)null } : new { kind = "webhook", key = (string?)null, eventId = (string?)source.ToString("D") },
                    filterExpression = "true", dispatch = new { mode = "coalesceLatest", windowSeconds = 60 } } } }));
                automation = saved.GetProperty("automationId").GetGuid(); child = saved.GetProperty("triggers")[0].GetProperty("triggerId").GetGuid();
                var events = first.Services.GetRequiredService<ITenantEventStore>();
                if (core)
                {
                    using var changed = await owner.PutAsJsonAsync(api + $"/agents/{instance}/persona", new { persona = agent.Instance.Persona with { Name = "Recovery fact" }, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1, expectedPersonaRevision = agent.Instance.PersonaRevision }); Assert.True(changed.IsSuccessStatusCode);
                    eventId = Assert.Single((await events.ListTenantCoreEventsAsync(tenant, instance, "instance.config_changed", 0, 100)).Items).Event.EventId;
                }
                else
                {
                    using var sent = new HttpRequestMessage(HttpMethod.Post, $"/api/v2/hooks/{tenant}/{source}") { Content = new StringContent("{\"eventId\":\"recovery-receipt\",\"data\":{\"bounded\":true}}", Encoding.UTF8, "application/json") };
                    sent.Headers.Authorization = new AuthenticationHeaderValue("Bearer", webhook.GetProperty("token").GetString()); eventId = (await Json(await owner.SendAsync(sent))).GetProperty("eventId").GetGuid();
                }
                var processor = first.Services.GetRequiredService<TenantEventProcessor>();
                if (phase != "receipt") await processor.RunOnceAsync();
                if (phase is "approval" or "approved" or "admitted")
                {
                    clock.Advance(TimeSpan.FromSeconds(61)); await processor.RunOnceAsync();
                    var request = Assert.Single((await first.Services.GetRequiredService<ITenantApprovalStore>().ListApprovalsAsync(tenant, null, null, new())).Items);
                    requestId = request.RequestId; hash = request.Operation.ActionHash; runId = request.RunId;
                    if (phase != "approval") await Decide(first, owner);
                    if (phase == "admitted") await first.Services.GetRequiredService<TenantApprovalService>().DispatchPendingAsync();
                }
                Assert.Equal(phase == "admitted" ? 1 : 0, (await first.Services.GetRequiredService<ITenantRunStore>().ListTenantSessionRunsAsync(tenant, null, null, 0, 100, instanceId: instance)).Total);
            }
            await using (var second = new OAuthFactory(root, sqlite: true, preserve: true, servicesOverride: Configure))
            {
                using var owner = ConnectedClient(second); await Login(owner, "owner@example.test", "explicit-synthetic-test-password"); await Stop(second);
                var processor = second.Services.GetRequiredService<TenantEventProcessor>();
                if (phase == "receipt") await processor.RunOnceAsync();
                clock.Advance(TimeSpan.FromSeconds(61)); await processor.RunOnceAsync();
                if (requestId is null)
                {
                    var request = Assert.Single((await second.Services.GetRequiredService<ITenantApprovalStore>().ListApprovalsAsync(tenant, null, null, new())).Items);
                    requestId = request.RequestId; hash = request.Operation.ActionHash; runId = request.RunId;
                }
                if (phase is "receipt" or "bucket" or "approval") await Decide(second, owner);
                await second.Services.GetRequiredService<TenantApprovalService>().DispatchPendingAsync(); await processor.RunOnceAsync();
                var run = Assert.Single((await second.Services.GetRequiredService<ITenantRunStore>().ListTenantSessionRunsAsync(tenant, null, null, 0, 100, instanceId: instance)).Items);
                Assert.Equal(runId, run.AgentRunId); Assert.Null(run.Owner.ProfileIdOrNull); Assert.Null(run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId);
                await second.Services.GetRequiredService<TenantEventReadAuthority>().RequireAsync(run, CancellationToken.None);
                Assert.Single((await second.Services.GetRequiredService<ITenantApprovalStore>().ListApprovalsAsync(tenant, null, null, new())).Items);
                var activity = await Json(await owner.GetAsync($"/api/v2/tenants/{tenant}/agents/{instance}/automations/{automation}/events"));
                var item = Assert.Single(activity.GetProperty("items").EnumerateArray()); Assert.Equal(eventId, item.GetProperty("eventId").GetGuid()); Assert.Equal(child, item.GetProperty("triggerId").GetGuid()); Assert.Equal(runId, item.GetProperty("runId").GetGuid());
                Assert.Equal(0, second.Outbound);
            }
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
