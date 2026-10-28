using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Events;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Logging;

namespace AgentCore.Api.Tests;

public sealed partial class TenantCredentialAccessTests
{
    public static TheoryData<bool, string> TenantEventCases
    {
        get
        {
            var cases = new TheoryData<bool, string>();
            foreach (var sqlite in new[] { false, true })
                foreach (var mode in new[] { "match", "filter-test", "filtered", "frozen", "late", "coalesce", "review", "source-review", "missing-read", "exception", "rotate", "raw-revoked", "accepted-rotate", "accepted-revoke", "terminal", "core", "core-review", "scoped-source-reader", "core-scoped-reader", "core-batch-reader" }) cases.Add(sqlite, mode);
            return cases;
        }
    }
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public Task Configured_Automation_event_completion_keeps_exact_Webhook_or_Core_admission_and_delivery_consent(bool sqlite, bool core) =>
        Tenant_event_receipt_snapshots_match_and_admit_only_the_exact_current_authorized_owner(sqlite, core ? "completion-core-review" : "completion-review");

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public Task Existing_conversation_event_Automation_keeps_exact_Webhook_or_Core_admission_and_normal_reply(bool sqlite, bool core) =>
        Tenant_event_receipt_snapshots_match_and_admit_only_the_exact_current_authorized_owner(sqlite, core ? "conversation-core-review" : "conversation-review");

    [Theory] [MemberData(nameof(TenantEventCases))]
    public Task Tenant_event_receipt_snapshots_match_and_admit_only_the_exact_current_authorized_owner(bool sqlite, string mode) =>
        EventPinWorkflow(sqlite, mode);

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public Task Event_receipts_freeze_context_pins_and_reject_retargeting_after_receipt(bool sqlite, bool core, bool changedPins) =>
        EventPinWorkflow(sqlite, core ? "core" : "qualified", true, changedPins);

    [Theory] [InlineData(false, "coalesce")] [InlineData(true, "coalesce")]
    [InlineData(false, "review")] [InlineData(true, "review")]
    public Task Pinned_event_coalescing_and_consent_keep_exact_context_without_duplicate_admission(bool sqlite, string mode) => EventPinWorkflow(sqlite, mode, pinned: true);

    [Fact]
    public Task Pinned_coalesced_event_recovers_exact_bucket_and_completes_once_after_SQLite_host_restart() =>
        EventPinWorkflow(true, "coalesce", pinned: true, restartCoalesced: true);

    private async Task EventPinWorkflow(bool sqlite, string mode, bool pinned = false, bool changedPins = false, bool restartCoalesced = false)
    {
        var conversation = mode.StartsWith("conversation-", StringComparison.Ordinal);
        if (conversation) mode = mode["conversation-".Length..];
        var configuredCompletion = mode.StartsWith("completion-", StringComparison.Ordinal);
        if (configuredCompletion) mode = mode["completion-".Length..];
        var execute = configuredCompletion || conversation;
        var coreSource = mode is "core" or "core-review" or "core-scoped-reader" or "core-batch-reader";
        var completionModel = new OwnedAutomationModel();
        var diagnostics = new AccountReadDiagnostics();
        var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        await using var factory = new OAuthFactory(sqlite: sqlite, preserve: restartCoalesced, execute: execute, model: conversation ? new AutomationConversationModel() : configuredCompletion ? completionModel : null, servicesOverride: services =>
        {
            services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock);
            if (conversation) services.AddSingleton<ILoggerProvider>(diagnostics);
            if (execute) foreach (var host in services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(AgentCore.Api.AgentRunHostedService)).ToArray()) services.Remove(host);
        });
        using var owner = ConnectedClient(factory); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        foreach (var service in factory.Services.GetServices<IHostedService>().Where(s => s is AgentCore.Api.AgentRunHostedService or AgentCore.Api.TriggerSchedulerHostedService)) await service.StopAsync(CancellationToken.None);
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var api = $"/api/v2/tenants/{tenant}"; var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
        var createdAgent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 36, expectedRevision = await Revision(owner, tenant) }));
        var instance = createdAgent.GetProperty("instance").GetProperty("instanceId").GetGuid();
        var agent = (await factory.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!;
        var access = factory.Services.GetRequiredService<TenantAccessService>();
        await GrantAutomationPrivateContext(factory, owner, tenant, human, agent);
        var permissions = new[] { "automations.run", "sessions.create", "agent-runs.create", "webhooks.read", "core-events.read" }.Concat(execute ? new[] { "agents.chat", "sessions.read", "agent-runs.read" } : Array.Empty<string>()).Where(p => !conversation || p != "sessions.create").Where(p => mode != "missing-read" || p != "webhooks.read").Where(p => mode != "exception" || p != "agent-runs.create").ToArray();
        var role = await access.SaveRoleAsync(human, tenant, null, "Event executor", permissions, await Revision(owner, tenant));
        await access.AssignRoleAsync(human, tenant, agent.PrincipalId, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
        if (mode is "review" or "source-review" or "core-review" or "scoped-source-reader" or "core-scoped-reader" or "core-batch-reader" or "rotate")
        {
            var key = mode == "source-review" ? "webhooks.read" : mode == "core-review" ? "core-events.read" : "agent-runs.create";
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/" + key, new { mode = "always", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
        }
        var webhook = await Json(await owner.PostAsJsonAsync(api + "/webhooks", new { displayName = "Receipt source", eventKey = "events.qualified", expectedRevision = await Revision(owner, tenant) }));
        var source = webhook.GetProperty("source").GetProperty("resourceId").GetGuid(); var token = webhook.GetProperty("token").GetString()!;
        Guid? completionDestination = execute ? (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, mode = "text", expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid() : null;
        var route = api + $"/agents/{instance}/automations";
        Guid? pinnedSpace = null, pinnedEnvironment = null;
        async Task PinContext()
        {
            var spaces = factory.Services.GetRequiredService<TenantSpaceService>();
            var shared = await spaces.CreateAsync(human, tenant, Guid.NewGuid(), "Event context", "", "Frozen event context.", await Revision(owner, tenant));
            await spaces.SetMemberAsync(human, tenant, shared.SpaceId, instance, "Event task", "", [], [], await Revision(owner, tenant), 1, 0);
            shared = await spaces.ReadAsync(human, tenant, shared.SpaceId);
            shared = await spaces.UpdateAsync(human, tenant, shared.SpaceId, shared.Name, shared.Description, shared.Instructions, true, await Revision(owner, tenant), shared.Revision);
            var contextRole = await access.SaveRoleAsync(human, tenant, null, "Exact event context", ["spaces.read", "environments.read", "environments.activate"], await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, agent.PrincipalId, contextRole.RoleId, TenantSpaceService.Scope(tenant, shared.SpaceId), await Revision(owner, tenant));
            pinnedSpace = shared.SpaceId;
            pinnedEnvironment = (await factory.Services.GetRequiredService<TenantEnvironmentService>().SaveAsync(human, tenant, shared.SpaceId, Guid.NewGuid(), "Event profile", "", await Revision(owner, tenant), shared.Revision, 0)).EnvironmentId;
        }
        if (pinned) await PinContext();
        var originalPinnedSpace = pinnedSpace; var originalPinnedEnvironment = pinnedEnvironment;
        var registrations = factory.Services.GetRequiredService<ITenantAutomationStore>(); var events = factory.Services.GetRequiredService<ITenantEventStore>();
        var processor = factory.Services.GetRequiredService<TenantEventProcessor>(); var runs = factory.Services.GetRequiredService<ITenantRunStore>();
        var receipts = factory.Services.GetRequiredService<ITenantWebhookStore>();
        object Trigger(string filter, Guid? id = null, long revision = 1) => new { kind = "event", triggerId = id, revision, enabled = true,
            source = coreSource ? new { kind = "builtin", key = (string?)"instance.config_changed", eventId = (string?)null } : new { kind = "webhook", key = (string?)null, eventId = (string?)source.ToString("D") },
            filterExpression = filter, dispatch = new { mode = (mode is "coalesce" or "core-batch-reader") ? "coalesceLatest" : "everyMatch", windowSeconds = (mode is "coalesce" or "core-batch-reader") ? (int?)60 : null } };
        async Task<Guid> Save(Guid? id = null, string filter = "true", string instructions = "Process this observed source once.")
        {
            var prior = id is null ? null : await registrations.FindTenantAutomationAsync(tenant, id.Value);
            var body = new { enabled = true, name = "Source-bound Automation", instructions, pinnedSpaceId = pinnedSpace, pinnedEnvironmentId = pinnedEnvironment, triggers = new[] { Trigger(filter, prior?.Triggers[0].TriggerId, prior?.Triggers[0].Revision ?? 1) },
                expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = agent.Instance.Revision, expectedAutomationRevision = prior?.Revision ?? 0, requiresTools = false, reportToSessionId = configuredCompletion ? completionDestination : null, targetSessionId = conversation ? completionDestination : null };
            using var response = id is null ? await owner.PostAsJsonAsync(route, body) : await owner.PutAsJsonAsync(route + $"/{id}", body);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync()); return (await Json(response)).GetProperty("automationId").GetGuid();
        }
        async Task<Guid> Send(string id)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v2/hooks/{tenant}/{source}") { Content = new StringContent(JsonSerializer.Serialize(new { eventId = id, data = new { orderId = "fixed-order", status = "received" } }), Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await owner.SendAsync(request); Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            return Assert.Single((await receipts.ListTenantWebhookReceiptsAsync(tenant, source, 0, 100)).Items, e => e.SourceEventId == id).EventId;
        }
        Guid? early = mode == "late" ? await Send("before-subscription") : null;
        var automationId = await Save(filter: mode == "filtered" ? "false" : "true");
        var automation = (await registrations.FindTenantAutomationAsync(tenant, automationId))!; var child = automation.Triggers[0];
        if (mode == "filter-test")
        {
            var beforeRevision = await Revision(owner, tenant);
            var sample = new { schemaVersion = 1, scope = new { agentInstanceId = Guid.NewGuid(), tenantId = Guid.NewGuid() }, data = new { status = "received" } };
            var result = await Json(await owner.PostAsJsonAsync(route + $"/filter-test?automationId={automationId}", new { expression = "event.data.status === 'received'", @event = sample }));
            Assert.True(result.GetProperty("matched").GetBoolean());
            var unsafeResult = await Json(await owner.PostAsJsonAsync(route + $"/filter-test?automationId={automationId}", new { expression = "event.data.status = 'changed'", @event = sample }));
            Assert.Equal("error", unsafeResult.GetProperty("status").GetString()); Assert.Equal(JsonValueKind.Null, unsafeResult.GetProperty("matched").ValueKind);
            var malformed = await Json(await owner.PostAsJsonAsync(route + $"/filter-test?automationId={automationId}", new { expression = "true", @event = new { schemaVersion = "1" } }));
            Assert.Equal("filter-envelope-schema", malformed.GetProperty("code").GetString());
            Assert.Equal(beforeRevision, await Revision(owner, tenant)); Assert.Equal(1, (await registrations.FindTenantAutomationAsync(tenant, automationId))!.Revision);
            Assert.Empty((await receipts.ListTenantWebhookReceiptsAsync(tenant, source, 0, 100)).Items);
            Assert.Empty((await runs.ListTenantSessionRunsAsync(tenant, null, null, 0, 100, instanceId: instance)).Items);
        }
        Guid eventId;
        if (coreSource)
        {
            var changed = await owner.PutAsJsonAsync(api + $"/agents/{instance}/persona", new { persona = agent.Instance.Persona with { Name = "Updated factual name" }, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = agent.Instance.Revision, expectedPersonaRevision = agent.Instance.PersonaRevision });
            Assert.True(changed.IsSuccessStatusCode, await changed.Content.ReadAsStringAsync());
            var fact = Assert.Single((await events.ListTenantCoreEventsAsync(tenant, instance, "instance.config_changed", 0, 100)).Items);
            Assert.Equal(TriggerOwner.ForTenant(tenant, instance, agent.PrincipalId), fact.Event.Owner); Assert.DoesNotContain("Updated factual name", fact.Event.DataJson);
            var globalCore = factory.Services.GetRequiredService<ICoreEventStore>();
            if (!sqlite && globalCore is InMemoryCoreEventStore memory)
                Assert.Throws<AgentCore.Application.Sessions.AgentCoreException>(() => memory.Append(fact.Event with { EventId = Guid.NewGuid(), DedupeKey = "concealed-tenant-owner", Owner = new TriggerOwner(instance, Guid.NewGuid()) }));
            if (sqlite)
            {
                await using var db = factory.Services.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>().CreateDbContext();
                CoreEventPersistence.Stage(db, fact.Event with { EventId = Guid.NewGuid(), DedupeKey = "concealed-tenant-owner", Owner = new TriggerOwner(instance, Guid.NewGuid()) });
                await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            }
            Assert.Empty(await globalCore.PendingAsync()); eventId = fact.Event.EventId;
            if ((mode is "core-scoped-reader" or "core-batch-reader"))
            {
                clock.Advance(TimeSpan.FromMilliseconds(1)); var currentAgent = (await factory.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!;
                using var another = await owner.PutAsJsonAsync(api + $"/agents/{instance}/persona", new { persona = currentAgent.Instance.Persona with { Name = "Another private configuration" }, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = currentAgent.Instance.Revision, expectedPersonaRevision = currentAgent.Instance.PersonaRevision });
                Assert.True(another.IsSuccessStatusCode, await another.Content.ReadAsStringAsync());
            }
            var corePage = await Json(await owner.GetAsync(api + $"/agents/{instance}/events/core/instance.config_changed?pageSize=1"));
            Assert.Equal((mode is "core-scoped-reader" or "core-batch-reader") ? 2 : 1, corePage.GetProperty("total").GetInt32()); Assert.Single(corePage.GetProperty("items").EnumerateArray());
            Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync(api + $"/events/core/{eventId}")).StatusCode);
        }
        else eventId = await Send("after-subscription");
        if (early is { } old) Assert.Null(await events.FindTenantEventDeliveryAsync(tenant, TriggerSourceKind.ApplicationEvent, old, child.TriggerId));
        if (mode == "frozen") await Save(automationId, "false", "Use the current parent intent with the receipt-time filter.");
        if (changedPins)
        {
            await PinContext(); agent = (await factory.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!;
            await Save(automationId);
        }
        await processor.RunOnceAsync();
        var kind = coreSource ? TriggerSourceKind.CoreEvent : TriggerSourceKind.ApplicationEvent;
        var delivery = (await events.FindTenantEventDeliveryAsync(tenant, kind, eventId, child.TriggerId))!;
        Assert.Null(await events.FindTenantEventDeliveryAsync(Guid.NewGuid(), kind, eventId, child.TriggerId));
        if (pinned)
        {
            Assert.Equal(originalPinnedSpace, delivery.Subscription.PinnedSpaceId); Assert.Equal(originalPinnedEnvironment, delivery.Subscription.PinnedEnvironmentId);
            if (changedPins)
            {
                Assert.Equal(EventMatchStatus.PolicySkipped, delivery.Status);
                Assert.Empty((await runs.ListTenantSessionRunsAsync(tenant, null, null, 0, 100, instanceId: instance)).Items);
                Assert.Equal(pinnedSpace, (await registrations.FindTenantAutomationAsync(tenant, automationId))!.PinnedSpaceId);
                clock.Advance(TimeSpan.FromMilliseconds(1));
                if (coreSource)
                {
                    agent = (await factory.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!;
                    var fresh = await owner.PutAsJsonAsync(api + $"/agents/{instance}/persona", new { persona = agent.Instance.Persona with { Name = "Future context event" },
                        expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = agent.Instance.Revision, expectedPersonaRevision = agent.Instance.PersonaRevision });
                    Assert.True(fresh.IsSuccessStatusCode, await fresh.Content.ReadAsStringAsync());
                }
                else await Send("new-context-after-edit");
                await processor.RunOnceAsync();
                var future = Assert.Single((await runs.ListTenantSessionRunsAsync(tenant, null, null, 0, 100, instanceId: instance)).Items);
                Assert.Equal(pinnedSpace, future.Admission.Configuration!.SpaceContext!.SpaceId);
                Assert.Equal(pinnedEnvironment, future.Admission.Configuration.SpaceContext.EnvironmentId);
                Assert.Equal(JsonSerializer.Serialize(delivery), JsonSerializer.Serialize(await events.FindTenantEventDeliveryAsync(tenant, kind, eventId, child.TriggerId)));
                return;
            }
        }
        var activityPath = route + $"/{automationId}/events?pageSize=1";
        var observedActivity = await Json(await owner.GetAsync(activityPath));
        Assert.True(observedActivity.GetProperty("total").GetInt32() >= 1);
        Assert.Single(observedActivity.GetProperty("items").EnumerateArray());
        Assert.Equal(coreSource ? "builtin" : "webhook", observedActivity.GetProperty("items")[0].GetProperty("sourceKind").GetString());
        if (mode is "filtered" or "missing-read")
        {
            Assert.Equal(mode == "filtered" ? EventMatchStatus.Filtered : EventMatchStatus.PolicySkipped, delivery.Status);
            Assert.Empty((await runs.ListTenantSessionRunsAsync(tenant, null, null, 0, 100, instanceId: instance)).Items); return;
        }
        if ((mode is "coalesce" or "core-batch-reader"))
        {
            Assert.Equal(EventMatchStatus.Coalesced, delivery.Status); clock.Advance(TimeSpan.FromSeconds(10)); if (!coreSource) await Send("same-window"); await processor.RunOnceAsync();
            clock.Advance(TimeSpan.FromSeconds(50)); var bucket = Assert.Single(await events.ListDueTenantEventBucketsAsync(clock.GetUtcNow(), 64)); Assert.Equal(2, bucket.Bucket.Sources.Count);
            if (restartCoalesced)
            {
                var retainedBucket = bucket.Bucket; var retainedRoot = factory.Root;
                await factory.DisposeAsync();
                await using var recoveredHost = new OAuthFactory(retainedRoot, sqlite: true, execute: true, model: completionModel, servicesOverride: services =>
                {
                    services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock);
                    foreach (var host in services.Where(d => d.ServiceType == typeof(IHostedService)
                        && (d.ImplementationType == typeof(AgentCore.Api.AgentRunHostedService) || d.ImplementationType == typeof(AgentCore.Api.TriggerSchedulerHostedService))).ToArray()) services.Remove(host);
                });
                var recoveredEvents = recoveredHost.Services.GetRequiredService<ITenantEventStore>();
                var recoveredBucket = Assert.Single(await recoveredEvents.ListDueTenantEventBucketsAsync(clock.GetUtcNow(), 64));
                Assert.Equal(JsonSerializer.Serialize(retainedBucket), JsonSerializer.Serialize(recoveredBucket.Bucket));
                Assert.Equal(originalPinnedSpace, recoveredBucket.Bucket.Subscription.PinnedSpaceId); Assert.Equal(originalPinnedEnvironment, recoveredBucket.Bucket.Subscription.PinnedEnvironmentId);
                var recoveredProcessor = recoveredHost.Services.GetRequiredService<TenantEventProcessor>();
                await recoveredProcessor.RunOnceAsync(); await recoveredProcessor.RunOnceAsync();
                var recoveredRuns = recoveredHost.Services.GetRequiredService<ITenantRunStore>();
                var recoveredRun = Assert.Single((await recoveredRuns.ListTenantSessionRunsAsync(tenant, null, null, 0, 100, instanceId: instance)).Items);
                Assert.Equal(originalPinnedSpace, recoveredRun.Admission.Configuration!.SpaceContext!.SpaceId); Assert.Equal(originalPinnedEnvironment, recoveredRun.Admission.Configuration.SpaceContext.EnvironmentId);
                Assert.Equal(ExecutionOrigin.ApplicationEvent, recoveredRun.Admission.ExecutionContext!.Origin);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var runtimeHost = recoveredHost.Services.GetRequiredService<AgentCore.Api.Realtime.TenantSessionRuntimeHost>();
                var releasing = new TaskCompletionSource<AgentRun>(TaskCreationOptions.RunContinuationsAsynchronously);
                var permitRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                runtimeHost.BeforeRuntimeRelease = active => { releasing.TrySetResult(active); return permitRelease.Task; };
                try
                {
                    do
                    {
                        await recoveredHost.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8, deadline.Token);
                        recoveredRun = (await recoveredRuns.FindTenantRunAsync(tenant, recoveredRun.AgentRunId, deadline.Token))!;
                        if (!recoveredRun.IsTerminal) await Task.Delay(20, deadline.Token);
                    } while (!recoveredRun.IsTerminal);
                    Assert.True(recoveredRun.Status == AgentRunStatus.Completed, $"{recoveredRun.Status}: {recoveredRun.Failure}");
                    var retiring = await releasing.Task.WaitAsync(deadline.Token);
                    var released = runtimeHost.WaitForGenerationReleaseAsync(retiring.SessionId, retiring.AgentRunId, retiring.Claim!.Generation, deadline.Token);
                    permitRelease.TrySetResult(); await released;
                }
                finally { permitRelease.TrySetResult(); runtimeHost.BeforeRuntimeRelease = null; }
                Assert.Single(completionModel.Requests); await recoveredProcessor.RunOnceAsync();
                Assert.Single((await recoveredRuns.ListTenantSessionRunsAsync(tenant, null, null, 0, 100, instanceId: instance)).Items);
                Assert.Equal(0, recoveredHost.Outbound); return;
            }
            await processor.RunOnceAsync();
        }
        if (mode is "review" or "source-review" or "core-review" or "scoped-source-reader" or "core-scoped-reader" or "core-batch-reader" or "exception" or "rotate")
        {
            Assert.Equal(mode == "core-batch-reader" ? EventMatchStatus.Coalesced : EventMatchStatus.AwaitingAdmission, delivery.Status); Assert.Empty((await runs.ListTenantSessionRunsAsync(tenant, null, null, 0, 100, instanceId: instance)).Items);
            var approvals = factory.Services.GetRequiredService<ITenantApprovalStore>(); var request = Assert.Single((await approvals.ListApprovalsAsync(tenant, null, null, new())).Items);
            Assert.Equal(mode == "exception" ? ApprovalKind.ExceptionalGrant : ApprovalKind.Review, request.Kind); Assert.NotNull(request.Operation.AutomationAdmission!.EventSource);
            Assert.Equal(mode == "source-review" ? "webhooks.read" : mode == "core-review" ? "core-events.read" : "agent-runs.create", request.Permission); Assert.Null(request.OriginalInitiatorPrincipalId);
            var detail = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}"));
            var changes = detail.GetProperty("review").GetProperty("changes").EnumerateArray().ToDictionary(c => c.GetProperty("field").GetString()!, c => c.GetProperty("after"));
            Assert.Equal((conversation ? 15 : 14) + (pinned ? 2 : 0), changes.Count); Assert.Contains(coreSource ? "Built-in event" : "Webhook event", changes["admissionSource"].GetString());
            if (conversation) Assert.Equal("Conversation revision 1 · input through entry sequence 0", changes["admissionConversation"].GetString());
            Assert.Contains(coreSource ? "core-events.read" : "webhooks.read", changes["admissionScope"].GetString());
            Assert.Equal("true", changes["admissionEventFilter"].GetString()); Assert.DoesNotContain(token, changes["admissionEventData"].GetString());
            if (mode is "scoped-source-reader" or "core-scoped-reader" or "core-batch-reader")
            {
                var identity = factory.Services.GetRequiredService<IdentityService>(); var account = await identity.RegisterAsync("event-consent-reader@example.test", "Source-scoped reader", "strong-event-consent-reader-password");
                var memberRole = await access.SaveRoleAsync(human, tenant, null, "Read consent prerequisites", ["approvals.read", "automations.read", "automations.run", "agents.read", "sessions.create", "agent-runs.create"], await Revision(owner, tenant));
                var invitation = await access.InviteAsync(human, tenant, account.LoginKey, memberRole.RoleId, await Revision(owner, tenant));
                var member = await access.AcceptInvitationAsync(account.AccountId, tenant, invitation.Invitation.InvitationId, invitation.Token);
                using var reader = ConnectedClient(factory); await Login(reader, account.LoginKey, "strong-event-consent-reader-password");
                var hidden = await Json(await reader.GetAsync(api + "/approvals/?pageSize=1")); Assert.Equal(0, hidden.GetProperty("total").GetInt32()); Assert.Empty(hidden.GetProperty("items").EnumerateArray());
                Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(api + $"/approvals/{request.RequestId}")).StatusCode);
                if (coreSource)
                {
                    var hiddenFacts = await Json(await reader.GetAsync(api + $"/agents/{instance}/events/core/instance.config_changed?pageSize=1"));
                    Assert.Equal(0, hiddenFacts.GetProperty("total").GetInt32()); Assert.Empty(hiddenFacts.GetProperty("items").EnumerateArray());
                    Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(api + $"/events/core/{eventId}")).StatusCode);
                }
                var hiddenActivity = await Json(await reader.GetAsync(activityPath)); Assert.Equal(0, hiddenActivity.GetProperty("total").GetInt32());
                var raw = await access.SaveRoleAsync(human, tenant, null, "Read this exact factual source", [coreSource ? "core-events.read" : "webhooks.read"], await Revision(owner, tenant));
                await access.AssignRoleAsync(human, tenant, member.PrincipalId, raw.RoleId, coreSource ? TenantEventAdmissionRules.Scope(tenant, eventId) : TenantWebhookRules.Scope(tenant, source), await Revision(owner, tenant));
                var visibleActivity = await Json(await reader.GetAsync(activityPath)); Assert.Equal(1, visibleActivity.GetProperty("total").GetInt32());
                var activityItem = Assert.Single(visibleActivity.GetProperty("items").EnumerateArray());
                Assert.Equal(eventId, activityItem.GetProperty("eventId").GetGuid()); Assert.Equal(JsonValueKind.Null, activityItem.GetProperty("runId").ValueKind);
                if (mode == "core-batch-reader")
                {
                    Assert.False(activityItem.GetProperty("batchReadable").GetBoolean());
                    foreach (var field in new[] { "bucketId", "batchCount", "occurrenceId", "approvalRequestId", "sessionId" }) Assert.Equal(JsonValueKind.Null, activityItem.GetProperty(field).ValueKind);
                    Assert.Equal(0, (await Json(await reader.GetAsync(api + "/approvals/?pageSize=1"))).GetProperty("total").GetInt32());
                    var second = Assert.Single((await events.ListTenantCoreEventsAsync(tenant, instance, "instance.config_changed", 0, 100)).Items, f => f.Event.EventId != eventId);
                    await access.AssignRoleAsync(human, tenant, member.PrincipalId, raw.RoleId, TenantEventAdmissionRules.Scope(tenant, second.Event.EventId), await Revision(owner, tenant));
                    var fullBatch = (await Json(await reader.GetAsync(activityPath))).GetProperty("items")[0];
                    Assert.True(fullBatch.GetProperty("batchReadable").GetBoolean()); Assert.Equal(2, fullBatch.GetProperty("batchCount").GetInt32());
                    Assert.Equal(request.RequestId, fullBatch.GetProperty("approvalRequestId").GetGuid());
                }
                var visible = await Json(await reader.GetAsync(api + "/approvals/?pageSize=1")); Assert.Equal(1, visible.GetProperty("total").GetInt32()); Assert.Single(visible.GetProperty("items").EnumerateArray());
                Assert.Empty((await Json(await reader.GetAsync(api + "/approvals/?page=1&pageSize=1"))).GetProperty("items").EnumerateArray());
                Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync(api + $"/approvals/{request.RequestId}")).StatusCode);
                if (coreSource && mode != "core-batch-reader")
                {
                    var rawPage = await Json(await reader.GetAsync(api + $"/agents/{instance}/events/core/instance.config_changed?pageSize=1"));
                    Assert.Equal(1, rawPage.GetProperty("total").GetInt32()); Assert.Single(rawPage.GetProperty("items").EnumerateArray());
                    Assert.Empty((await Json(await reader.GetAsync(api + $"/agents/{instance}/events/core/instance.config_changed?page=1&pageSize=1"))).GetProperty("items").EnumerateArray());
                    Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync(api + $"/events/core/{eventId}")).StatusCode);
                    var otherFact = Assert.Single((await events.ListTenantCoreEventsAsync(tenant, instance, "instance.config_changed", 0, 100)).Items, f => f.Event.EventId != eventId);
                    Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(api + $"/events/core/{otherFact.Event.EventId}")).StatusCode);
                }
            }
            if (mode == "rotate")
            {
                Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/webhooks/{source}/rotate", new { expectedRevision = await Revision(owner, tenant), expectedSourceRevision = 1 })).StatusCode);
                await Assert.ThrowsAsync<SecurityConflictException>(() => factory.Services.GetRequiredService<TenantAutomationOperationTarget>().RequireAsync(request, CancellationToken.None).AsTask()); return;
            }
            using var decision = await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = "approve", actionHash = request.Operation.ActionHash, expectedRevision = await Revision(owner, tenant), expectedRequestRevision = request.Revision });
            Assert.True(decision.IsSuccessStatusCode, await decision.Content.ReadAsStringAsync()); await factory.Services.GetRequiredService<TenantApprovalService>().DispatchPendingAsync();
        }
        var run = Assert.Single((await runs.ListTenantSessionRunsAsync(tenant, null, null, 0, 100, instanceId: instance)).Items);
        Assert.Equal(coreSource ? ExecutionOrigin.CoreEvent : ExecutionOrigin.ApplicationEvent, run.Admission.ExecutionContext!.Origin); Assert.Equal(kind, run.Admission.TaskIntent!.EventSourceKind);
        Assert.Null(run.Owner.ProfileIdOrNull); Assert.Null(run.Admission.ExecutionContext.OriginalInitiatorPrincipalId);
        if (pinned)
        {
            Assert.Equal(originalPinnedSpace, run.Admission.Configuration!.SpaceContext!.SpaceId);
            Assert.Equal(originalPinnedEnvironment, run.Admission.Configuration.SpaceContext.EnvironmentId);
            var occurrencePins = (await registrations.FindTenantOccurrenceAsync(tenant, run.Admission.Activation.TriggerOccurrenceId!.Value))!;
            Assert.Equal(originalPinnedSpace, occurrencePins.PinnedSpaceId); Assert.Equal(originalPinnedEnvironment, occurrencePins.PinnedEnvironmentId);
        }
        if (mode == "frozen") Assert.Equal("Use the current parent intent with the receipt-time filter.", run.Admission.TaskIntent.Text);
        var admittedActivity = Assert.Single((await Json(await owner.GetAsync(route + $"/{automationId}/events?pageSize=100"))).GetProperty("items").EnumerateArray(), row => row.GetProperty("eventId").GetGuid() == eventId);
        Assert.Equal(run.AgentRunId, admittedActivity.GetProperty("runId").GetGuid()); Assert.Equal("admitted", admittedActivity.GetProperty("status").GetString());
        await factory.Services.GetRequiredService<TenantEventReadAuthority>().RequireAsync(run, CancellationToken.None);
        if (mode is "accepted-rotate" or "accepted-revoke")
        {
            using var changed = await owner.PostAsJsonAsync(api + $"/webhooks/{source}/{(mode == "accepted-rotate" ? "rotate" : "revoke")}", new { expectedRevision = await Revision(owner, tenant), expectedSourceRevision = 1 });
            Assert.True(changed.IsSuccessStatusCode, await changed.Content.ReadAsStringAsync());
            await factory.Services.GetRequiredService<TenantEventReadAuthority>().RequireAsync(run, CancellationToken.None);
        }
        if (mode == "terminal")
        {
            var runtime = factory.Services.GetRequiredService<ITenantRuntimeStore>();
            var claimed = await runtime.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentCore.Application.Execution.AgentRunCommand.Claim(run.Revision, clock.GetUtcNow(), Guid.NewGuid(), clock.GetUtcNow().AddMinutes(5)), Guid.NewGuid());
            await runtime.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentCore.Application.Execution.AgentRunCommand.Complete(claimed.Revision, clock.GetUtcNow(), claimed.Claim!.Generation, "No visible reply", AgentRunOutcomeKind.NoAction, null), Guid.NewGuid());
            var fact = Assert.Single((await events.ListTenantCoreEventsAsync(tenant, instance, "run.completed", 0, 100)).Items).Event;
            Assert.Equal(run.AgentRunId, fact.RootAgentRunId); Assert.Equal(1, fact.TriggerDepth); Assert.Contains(automationId, fact.VisitedAutomationIds!);
            Assert.DoesNotContain("fixed-order", fact.DataJson); Assert.DoesNotContain("No visible reply", fact.DataJson);
        }
        if (mode == "raw-revoked")
        {
            var runtime = factory.Services.GetRequiredService<ITenantRuntimeStore>();
            var claimed = await runtime.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentCore.Application.Execution.AgentRunCommand.Claim(run.Revision, clock.GetUtcNow(), Guid.NewGuid(), clock.GetUtcNow().AddMinutes(5)), Guid.NewGuid());
            await access.SaveRoleAsync(human, tenant, role.RoleId, role.Name, permissions.Where(p => p != "webhooks.read").ToArray(), await Revision(owner, tenant));
            var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => factory.Services.GetRequiredService<TenantEventReadAuthority>().RequireAsync(run, CancellationToken.None).AsTask()); Assert.Equal("event_source_read_required", denied.ReasonCode);
            await Assert.ThrowsAsync<AccessDeniedException>(() => runtime.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
                new AgentCore.Application.Execution.AgentRunCommand.Complete(claimed.Revision, clock.GetUtcNow(), claimed.Claim!.Generation, "No visible reply", AgentRunOutcomeKind.NoAction, null), Guid.NewGuid()).AsTask());
        }
        await processor.RunOnceAsync(); Assert.Single((await runs.ListTenantSessionRunsAsync(tenant, null, null, 0, 100, instanceId: instance)).Items);
        if (execute)
        {
            var execution = factory.Services.GetRequiredService<ITenantRunExecution>();
            async Task<AgentRun> Execute(Guid id)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)); AgentRun result;
                try { do { await execution.ExecuteRunnableAsync(8, deadline.Token); result = (await runs.FindTenantRunAsync(tenant, id, deadline.Token))!; if (!result.IsTerminal) await Task.Delay(20, deadline.Token); } while (!result.IsTerminal); }
                catch (OperationCanceledException)
                {
                    var stalled = await runs.FindTenantRunAsync(tenant, id);
                    Assert.Fail($"Run {stalled?.Status}: {stalled?.Failure?.Code}; " + string.Join("\n", diagnostics.Failures.Take(3))); throw;
                }
                Assert.True(result.Status == AgentRunStatus.Completed, $"{result.Status}: {result.Failure?.Code}"); return result;
            }
            var childRun = await Execute(run.AgentRunId);
            if (conversation)
            {
                Assert.Equal(completionDestination, childRun.SessionId); Assert.Equal(AgentRunOutputContract.ConversationResponse, childRun.Admission.OutputContract);
                Assert.Empty(childRun.Admission.Activation.SourceEntryIds); Assert.Equal("An exact conversation task completed.", childRun.Result!.Text);
                var terminal = Assert.Single((await events.ListTenantCoreEventsAsync(tenant, instance, "run.completed", 0, 100)).Items).Event;
                Assert.Equal(childRun.AgentRunId, terminal.RootAgentRunId); Assert.Equal(1, terminal.TriggerDepth); Assert.Contains(automationId, terminal.VisitedAutomationIds!);
                var session = (await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(tenant, childRun.SessionId))!;
                Assert.Equal(human, session.Owner.CreatedByPrincipalId); Assert.Equal(SessionOriginKind.UserChat, session.Snapshot.Origin.Kind);
                var history = (await Json(await owner.GetAsync(api + $"/sessions/{childRun.SessionId}/messages"))).GetProperty("items").EnumerateArray().ToArray();
                Assert.Single(history); Assert.Equal("assistant", history[0].GetProperty("role").GetString()); Assert.Equal("completed", history[0].GetProperty("status").GetString());
                Assert.Single((await factory.Services.GetRequiredService<ITenantSessionStore>().ListTenantSessionsAsync(tenant, null, 0, 100)).Items); Assert.Equal(0, factory.Outbound); return;
            }
            var completions = factory.Services.GetRequiredService<TenantBackgroundCompletionService>(); await completions.AdmitPendingAsync(10, CancellationToken.None);
            if (mode == "review")
            {
                var reportConsent = Assert.Single((await factory.Services.GetRequiredService<ITenantApprovalStore>().ListApprovalsAsync(tenant, null, null, new(PageSize: 100))).Items, r => r.Status == TenantApprovalStatus.Pending);
                Assert.NotNull(reportConsent.Operation.BackgroundReport); Assert.Equal(completionDestination, reportConsent.Operation.BackgroundReport.ParentSessionId);
                var approved = await owner.PostAsJsonAsync(api + $"/approvals/{reportConsent.RequestId}/decision", new { decision = "approve", actionHash = reportConsent.Operation.ActionHash, expectedRevision = await Revision(owner, tenant), expectedRequestRevision = reportConsent.Revision });
                Assert.True(approved.IsSuccessStatusCode, await approved.Content.ReadAsStringAsync()); await factory.Services.GetRequiredService<TenantApprovalService>().DispatchPendingAsync();
            }
            var report = Assert.Single((await runs.ListTenantSessionRunsAsync(tenant, completionDestination, null, 0, 100)).Items);
            Assert.Null(report.Admission.ExecutionContext!.OriginalInitiatorPrincipalId); Assert.Equal(childRun.AgentRunId, report.Admission.ExecutionContext.ParentRunId);
            var delivered = await Execute(report.AgentRunId); Assert.Contains("Owned automation completed.", delivered.Result!.Text); Assert.Single(completionModel.Requests);
            Assert.Equal(CompletionInboxStatus.Delivered, (await runs.FindTenantCompletionAsync(childRun.Owner, childRun.AgentRunId))!.Status);
            Assert.Equal(childRun.Revision, (await runs.FindTenantRunAsync(tenant, childRun.AgentRunId))!.Revision);
            await completions.AdmitPendingAsync(10, CancellationToken.None); Assert.Single((await runs.ListTenantSessionRunsAsync(tenant, completionDestination, null, 0, 100)).Items);
        }
    }
}
