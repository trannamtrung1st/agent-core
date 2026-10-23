using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Context;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Api.Tests;
public sealed partial class TenantSessionAccessTests
{
    public static IEnumerable<object[]> IndependentPrivateSkillCases => (from sqlite in new[] { false, true }
        from origin in new[] { "scheduled", "manual", "core", "webhook" }
        from mode in new[] { "direct", "approve", "reject" }
        select new object[] { sqlite, origin, mode, false }).Concat(
        from origin in new[] { "core", "webhook" }
        from mode in new[] { "approve", "reject" }
        select new object[] { true, origin, mode, true }).Concat(
        from sqlite in new[] { false, true }
        from origin in new[] { "core", "webhook" }
        select new object[] { sqlite, origin, "no_read", false });
    [Theory(Timeout = 120000)] [MemberData(nameof(IndependentPrivateSkillCases))]
    public async Task Independent_Automation_loads_private_Skills_with_actual_origin_exact_read_Review_and_no_Chat_or_management_authority(bool sqlite, string origin, string mode, bool restart)
    {
        var root = Path.Combine(Path.GetTempPath(), "independent-private-skill-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow); var model = new PrivateSkillModel(false, true); var logs = new ManagementMutationLogs();
        void Configure(IServiceCollection services) { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock); services.AddSingleton<ILoggerProvider>(logs); }
        WebApplicationFactory<Program>? recreated = null; HttpClient? recreatedOwner = null;
        try
        {
            await using WebApplicationFactory<Program> first = sqlite ? new NativeTenantSqliteFactory(root, model, Configure) : new NativeTenantMemoryFactory(root, model, Configure);
            var host = first;
            using var firstOwner = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") }); var owner = firstOwner;
            var setup = await StartTenantBackground(host, owner, submit: false, fileCapabilities: [], independentTask: true);
            var tenant = setup.Tenant; var api = $"/api/v2/tenants/{tenant}";
            var parent = (await host.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(tenant, setup.Session))!;
            var instance = parent.Owner.AgentInstanceId; var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/agents/{instance}/skills/commands", new { operation = "create", expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1,
                input = new { id = "private-procedure", name = "Private procedure", description = "Private description sentinel", procedure = PrivateSkillProcedure,
                    projection = "OnDemand", enabled = true, requiredCapabilities = new[] { "chat.respond" } } })).StatusCode);
            var access = host.Services.GetRequiredService<TenantAccessService>();
            var role = await access.SaveRoleAsync(human, tenant, null, "Independent private procedure executor",
                ["automations.run", "agents.read", .. mode == "no_read" ? Array.Empty<string>() : new[] { "skills.read" },
                    .. origin == "core" ? new[] { "core-events.read" } : origin == "webhook" ? new[] { "webhooks.read" } : Array.Empty<string>()], await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, parent.Owner.ExecutorPrincipalId, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
            if (mode is "approve" or "reject") Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/skills.read", new { mode = "always", independentReview = false,
                expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
            while (logs.Errors.TryDequeue(out _)) { }
            var id = origin is "core" or "webhook"
                ? await AdmitPrivateSkillEventTask(host, owner, tenant, instance, clock, origin == "core")
                : await TenantCredentialAccessTests.AdmitIndependentTask(host, owner, tenant, instance, "Use the exact instance:private-procedure Skill for this admitted task.", clock, origin);
            var runs = host.Services.GetRequiredService<ITenantRunStore>();
            string Diagnostic(AgentRun value) => $"{value.Status}: {value.Failure?.Code}: {value.Failure?.Summary}; modelCalls={model.Requests.Count}; tools={string.Join(" | ", model.Requests.SelectMany(r => r.Messages).Where(m => m.Role == ModelRole.Tool).Select(m => m.Name + ":" + m.Text))}; logs={string.Join(" | ", logs.Errors)}";
            async Task<AgentRun> Pump(bool successor = false)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
                while (true)
                {
                    await host.Services.GetRequiredService<TenantApprovalService>().DispatchPendingAsync(deadline.Token);
                    await host.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8, deadline.Token);
                    var current = (await CurrentApprovalExecution(runs, tenant, id))!;
                    if ((!successor || current.AgentRunId != id) && (current.IsTerminal || !successor && current.Status == AgentRunStatus.WaitingForApproval)) return current;
                    if (current.Status == AgentRunStatus.WaitingToRetry && current.NextRetryAtUtc is { } retry && retry > clock.GetUtcNow()) clock.Advance(retry - clock.GetUtcNow());
                    try { await Task.Delay(20, deadline.Token); }
                    catch (TaskCanceledException) { Assert.Fail(Diagnostic(current)); throw; }
                }
            }
            var run = await Pump();
            var initiator = origin == "manual" ? human : (Guid?)null;
            var expectedOrigin = origin switch { "scheduled" => ExecutionOrigin.ScheduledOccurrence, "manual" => ExecutionOrigin.ManualInvocation,
                "core" => ExecutionOrigin.CoreEvent, "webhook" => ExecutionOrigin.ApplicationEvent, _ => throw new ArgumentException(nameof(origin)) };
            Assert.Equal(initiator, run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId);
            Assert.Equal(expectedOrigin, run.Admission.ExecutionContext.Origin);
            Assert.Null(run.Owner.ProfileIdOrNull);
            if (mode is "approve" or "reject")
            {
                Assert.True(run.Status == AgentRunStatus.WaitingForApproval, Diagnostic(run)); Assert.Equal(0, run.SkillLoadCount);
                Assert.All(model.Requests, r => Assert.DoesNotContain(PrivateSkillProcedure, r.Context!.Text(ContextSectionId.InstanceSkills)));
                var approvals = host.Services.GetRequiredService<ITenantApprovalStore>(); var request = (await approvals.FindApprovalAsync(tenant, run.ApprovalRequestId!.Value))!;
                Assert.Equal(initiator, request.OriginalInitiatorPrincipalId); Assert.Equal(ToolCatalog.SkillsLoad, request.Operation.ManagementRead!.ToolName);
                if (restart)
                {
                    firstOwner.Dispose(); await first.DisposeAsync();
                    model = new PrivateSkillModel(false, true);
                    recreated = new NativeTenantSqliteFactory(root, model, Configure); host = recreated;
                    recreatedOwner = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") }); owner = recreatedOwner;
                    await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
                    runs = host.Services.GetRequiredService<ITenantRunStore>(); approvals = host.Services.GetRequiredService<ITenantApprovalStore>();
                    var retained = (await runs.FindTenantRunAsync(tenant, id))!;
                    Assert.Equal(AgentRunStatus.WaitingForApproval, retained.Status); Assert.Equal(0, retained.SkillLoadCount); Assert.Empty(model.Requests);
                    Assert.Equal(expectedOrigin, retained.Admission.ExecutionContext!.Origin); Assert.Null(retained.Admission.ExecutionContext.OriginalInitiatorPrincipalId);
                    var durable = (await approvals.FindApprovalAsync(tenant, request.RequestId))!;
                    Assert.Equal(request.Operation.ActionHash, durable.Operation.ActionHash); Assert.Equal(request.Operation.ManagementRead, durable.Operation.ManagementRead); request = durable;
                }
                Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync(api + $"/approvals/{request.RequestId}")).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = mode, actionHash = request.Operation.ActionHash,
                    expectedRevision = await Revision(owner, tenant), expectedRequestRevision = request.Revision })).StatusCode);
                run = await Pump(successor: true);
                Assert.Null(await approvals.FindOperationGrantAsync(tenant, request.RequestId));
            }
            Assert.True(run.Status == AgentRunStatus.Completed, Diagnostic(run));
            var activated = mode is "direct" or "approve";
            Assert.Equal(activated, run.ActiveSkillKeys.Contains(PrivateSkillKey)); Assert.Equal(activated ? 1 : 0, run.SkillLoadCount);
            Assert.Equal(initiator, run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId); Assert.NotEqual(setup.Session, run.SessionId);
            Assert.Equal(origin == "core" ? 3 : 2, run.Admission.Configuration!.InstanceRevision);
            Assert.Equal(activated, model.Requests.Any(r => r.Context!.Text(ContextSectionId.InstanceSkills).Contains(PrivateSkillProcedure, StringComparison.Ordinal)));
            if (mode is "direct" or "no_read") Assert.Empty((await Json(await owner.GetAsync(api + "/approvals"))).GetProperty("items").EnumerateArray());
            Assert.DoesNotContain(model.Requests.SelectMany(r => r.Tools ?? []), t => t.Name.StartsWith("management.") || t.Name.StartsWith("harness.") || t.Name == ToolCatalog.BackgroundStart);
        }
        finally { recreatedOwner?.Dispose(); if (recreated is not null) await recreated.DisposeAsync(); SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private static async Task<Guid> AdmitPrivateSkillEventTask(WebApplicationFactory<Program> host, HttpClient owner,
        Guid tenant, Guid instance, FakeTimeProvider clock, bool core)
    {
        var api = $"/api/v2/tenants/{tenant}";
        var agent = (await host.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!;
        Guid? source = null; string? token = null;
        if (!core)
        {
            var webhook = await Json(await owner.PostAsJsonAsync(api + "/webhooks", new { displayName = "Private Skill event source",
                eventKey = "private.skill.activation", expectedRevision = await Revision(owner, tenant) }));
            source = webhook.GetProperty("source").GetProperty("resourceId").GetGuid(); token = webhook.GetProperty("token").GetString();
        }
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var created = await Json(await owner.PostAsJsonAsync(api + $"/agents/{instance}/automations", new {
            enabled = true, name = "Independent event private procedure", instructions = "Use the exact instance:private-procedure Skill for this admitted task.", requiresTools = true,
            triggers = new[] { new { kind = "event", enabled = true,
                source = core ? new { kind = "builtin", key = (string?)"instance.config_changed", eventId = (string?)null }
                    : new { kind = "webhook", key = (string?)null, eventId = (string?)source!.Value.ToString("D") },
                filterExpression = "true", dispatch = new { mode = "everyMatch" } } },
            expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = agent.Instance.Revision }));
        var automation = created.GetProperty("automationId").GetGuid();
        var events = host.Services.GetRequiredService<ITenantEventStore>(); var registrations = host.Services.GetRequiredService<ITenantAutomationStore>();
        var registration = (await registrations.FindTenantAutomationAsync(tenant, automation))!;
        clock.Advance(TimeSpan.FromMilliseconds(1)); Guid eventId;
        if (core)
        {
            var before = (await events.ListTenantCoreEventsAsync(tenant, instance, "instance.config_changed", 0, 100)).Items.Select(row => row.Event.EventId).ToHashSet();
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + $"/agents/{instance}/persona", new {
                persona = agent.Instance.Persona with { Name = "Private event fixture" }, expectedRevision = await Revision(owner, tenant),
                expectedInstanceRevision = agent.Instance.Revision, expectedPersonaRevision = agent.Instance.PersonaRevision })).StatusCode);
            eventId = Assert.Single((await events.ListTenantCoreEventsAsync(tenant, instance, "instance.config_changed", 0, 100)).Items,
                row => !before.Contains(row.Event.EventId)).Event.EventId;
        }
        else
        {
            using var ingress = new HttpRequestMessage(HttpMethod.Post, $"/api/v2/hooks/{tenant}/{source}") {
                Content = new StringContent(JsonSerializer.Serialize(new { eventId = "private-skill-input", data = new { task = "Untrusted event data does not grant a procedure or management authority." } }), Encoding.UTF8, "application/json") };
            ingress.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            Assert.Equal(HttpStatusCode.Accepted, (await owner.SendAsync(ingress)).StatusCode);
            eventId = Assert.Single((await host.Services.GetRequiredService<ITenantWebhookStore>().ListTenantWebhookReceiptsAsync(tenant, source!.Value, 0, 100)).Items).EventId;
        }
        await host.Services.GetRequiredService<TenantEventProcessor>().RunOnceAsync();
        var kind = core ? TriggerSourceKind.CoreEvent : TriggerSourceKind.ApplicationEvent;
        var delivery = (await events.FindTenantEventDeliveryAsync(tenant, kind, eventId, registration.Triggers[0].TriggerId))!;
        var occurrence = (await registrations.FindTenantOccurrenceAsync(tenant, delivery.ReservedOccurrenceId!.Value))!;
        var run = (await host.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(tenant, occurrence.AcceptedAgentRunId!.Value))!;
        Assert.Equal(kind, run.Admission.TaskIntent!.EventSourceKind); Assert.Null(run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId);
        Assert.Equal(run.AgentRunId, Assert.Single((await events.ListTenantEventActivityAsync(tenant, instance, automation, new(null, null, null), 0, 100)).Items).Occurrence!.AcceptedAgentRunId);
        return run.AgentRunId;
    }
}
