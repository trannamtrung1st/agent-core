using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Events;
using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed class MultiTriggerAutomationJourneyTests
{
    [Fact(Timeout = 60000)]
    public async Task Persisted_schedule_events_schedule_switches_survive_each_sqlite_reopen()
    {
        var path = Path.Combine(Path.GetTempPath(), $"trigger-mode-{Guid.NewGuid():N}.db");
        Guid instanceId; string automationId; string scheduleId; string eventId;
        var timing = new AutomationTiming("daily", LocalTime: "14:30");
        string Route(Guid id) => $"/api/v2/admin/agent-instances/{id}/automations";
        await using (var host = new ExperienceHost(path))
        {
            instanceId = (await host.Services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 22)).InstanceId;
            using var client = TestOwnerCapability.CreateOwnerClient(host);
            var request = new AutomationRequest(0, false, "Switch modes", "Review each signal", [new("schedule", Schedule: timing, TriggerId: Guid.NewGuid().ToString())],
                ExecutionTarget: new("backgroundSession"), CompletionDelivery: new("none"));
            var response = await client.PostAsJsonAsync(Route(instanceId), request);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            var saved = (await response.Content.ReadFromJsonAsync<AutomationResponse>())!;
            automationId = saved.AutomationId; scheduleId = Assert.Single(saved.Triggers!).TriggerId!;
        }
        await using (var host = new ExperienceHost(path))
        {
            using var client = TestOwnerCapability.CreateOwnerClient(host);
            var saved = Assert.Single((await client.GetFromJsonAsync<AutomationReview>(Route(instanceId)))!.Items);
            Assert.Equal(scheduleId, Assert.Single(saved.Triggers!).TriggerId);
            var request = new AutomationRequest(saved.Revision, false, saved.Name, saved.Instructions,
                [new("event", Source: new("builtin", "run.failed"), FilterExpression: "true", TriggerId: Guid.NewGuid().ToString()),
                 new("event", Source: new("builtin", "session.ended"), FilterExpression: "false", TriggerId: Guid.NewGuid().ToString())],
                ExecutionTarget: saved.ExecutionTarget, CompletionDelivery: saved.CompletionDelivery);
            var response = await client.PutAsJsonAsync(Route(instanceId) + "/" + automationId, request); response.EnsureSuccessStatusCode();
            var changed = (await response.Content.ReadFromJsonAsync<AutomationResponse>())!;
            Assert.Equal(2, changed.Triggers!.Count);
            Assert.DoesNotContain(changed.Triggers, t => t.TriggerId == scheduleId);
            eventId = changed.Triggers[0].TriggerId!;
        }
        await using (var host = new ExperienceHost(path))
        {
            using var client = TestOwnerCapability.CreateOwnerClient(host);
            var saved = Assert.Single((await client.GetFromJsonAsync<AutomationReview>(Route(instanceId)))!.Items);
            Assert.Equal(2, saved.Triggers!.Count); Assert.Contains(saved.Triggers, t => t.TriggerId == eventId);
            Assert.All(saved.Triggers, t => Assert.Equal("event", t.Kind));
            var request = new AutomationRequest(saved.Revision, false, saved.Name, saved.Instructions, [new("schedule", Schedule: timing, TriggerId: Guid.NewGuid().ToString())],
                ExecutionTarget: saved.ExecutionTarget, CompletionDelivery: saved.CompletionDelivery);
            var response = await client.PutAsJsonAsync(Route(instanceId) + "/" + automationId, request); response.EnsureSuccessStatusCode();
            scheduleId = Assert.Single((await response.Content.ReadFromJsonAsync<AutomationResponse>())!.Triggers!).TriggerId!;
        }
        await using (var host = new ExperienceHost(path))
        {
            using var client = TestOwnerCapability.CreateOwnerClient(host);
            var saved = Assert.Single((await client.GetFromJsonAsync<AutomationReview>(Route(instanceId)))!.Items);
            Assert.Equal(automationId, saved.AutomationId);
            var child = Assert.Single(saved.Triggers!);
            Assert.Equal(scheduleId, child.TriggerId); Assert.Equal("schedule", child.Kind);
            Assert.Equal("14:30", child.Schedule!.LocalTime);
            await using var db = await host.Services.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>().CreateDbContextAsync();
            Assert.Single(await db.AutomationTriggers.ToListAsync());
        }
    }

    [Fact(Timeout = 60000)]
    public async Task Mixed_sources_freeze_child_filters_preserve_identity_and_recover_without_replay()
    {
        var path = Path.Combine(Path.GetTempPath(), $"multi-trigger-{Guid.NewGuid():N}.db");
        Guid instanceId; Guid automationId; Guid builtinId; Guid webhookId;
        await using (var host = new ExperienceHost(path))
        {
            var s = host.Services;
            instanceId = (await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 22)).InstanceId;
            using var client = TestOwnerCapability.CreateOwnerClient(host);
            var catalog = await client.GetFromJsonAsync<JsonElement>("/api/v2/admin/connections/events/catalog?kind=builtin");
            Assert.Equal(6, catalog.GetArrayLength());
            Assert.All(catalog.EnumerateArray(), e => { Assert.Equal("builtin", e.GetProperty("source").GetProperty("kind").GetString()); Assert.Equal(JsonValueKind.Null, e.GetProperty("webhook").ValueKind); });
            var created = await client.PostAsJsonAsync("/api/v2/admin/connections/events", new { displayName = "Orders", eventKey = "multi.orders" });
            created.EnsureSuccessStatusCode();
            var resource = (await created.Content.ReadFromJsonAsync<AdminWebhookEventCredentialResponse>())!;
            builtinId = Guid.NewGuid(); webhookId = Guid.NewGuid();
            var children = new[] {
                new AutomationTriggerDto("event", Source: new("builtin", "instance.config_changed"), FilterExpression: "true", TriggerId: builtinId.ToString()),
                new AutomationTriggerDto("event", Source: new("webhook", EventId: resource.EventId), FilterExpression: "event.data.total >= 100", TriggerId: webhookId.ToString())
            };
            var route = $"/api/v2/admin/agent-instances/{instanceId}/automations";
            var draft = new AutomationRequest(0, true, "Independent OR", "Review the signal", children, ExecutionTarget: new("backgroundSession"), CompletionDelivery: new("none"));
            var saved = await client.PostAsJsonAsync(route, draft); saved.EnsureSuccessStatusCode();
            var parent = (await saved.Content.ReadFromJsonAsync<AutomationResponse>())!;
            automationId = Guid.Parse(parent.AutomationId);
            var owner = new TriggerOwner(instanceId, LocalUserProfile.Id);
            var receiptId = Guid.NewGuid();
            await using (var db = await s.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>().CreateDbContextAsync())
            {
                CoreEventPersistence.Stage(db, new(receiptId, $"multi:{receiptId}", owner, "instance.config_changed", DateTimeOffset.UtcNow, "{\"revision\":2}"));
                await db.SaveChangesAsync();
            }
            var events = s.GetRequiredService<ICoreEventStore>();
            await events.SnapshotAsync(receiptId, [new(automationId, owner, 1, "true", new()) { TriggerId = builtinId }]);
            var edit = draft with { ExpectedRevision = parent.Revision, Triggers = parent.Triggers!.Select(t => t.TriggerId == builtinId.ToString() ? t with { FilterExpression = "false" } : t).Reverse().ToArray() };
            var edited = await client.PutAsJsonAsync(route + "/" + automationId, edit); edited.EnsureSuccessStatusCode();
            var current = (await edited.Content.ReadFromJsonAsync<AutomationResponse>())!;
            Assert.Equal(2, current.Triggers!.Single(t => t.TriggerId == builtinId.ToString()).Revision);
            Assert.Equal(1, current.Triggers!.Single(t => t.TriggerId == webhookId.ToString()).Revision);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(route + "/" + automationId, edit)).StatusCode);
            Assert.Equal(1, await s.GetRequiredService<CoreEventDispatcher>().RunOnceAsync());
            Assert.Equal(EventMatchStatus.Admitted, Assert.Single(await events.DeliveriesAsync(receiptId)).Status);
            var ingress = s.GetRequiredService<ExternalEventIngress>();
            var payload = Encoding.UTF8.GetBytes("{\"eventId\":\"order-1\",\"rootAgentRunId\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\",\"triggerDepth\":4,\"data\":{\"total\":150}}");
            Assert.Equal(ExternalEventIngressKind.Admitted, (await ingress.AdmitAsync(resource.EventKey, resource.Token, payload)).Kind);
            Assert.Equal(ExternalEventIngressKind.Duplicate, (await ingress.AdmitAsync(resource.EventKey, resource.Token, payload)).Kind);
            var occurrences = await s.GetRequiredService<ITriggerStore>().ListByDispositionAsync(OccurrenceRoutingDisposition.Pending, 20);
            Assert.Equal(2, occurrences.Count);
            Assert.Contains(occurrences, o => o.TriggerId == builtinId && o.TriggerRevision == 1);
            var external = Assert.Single(occurrences, o => o.TriggerId == webhookId);
            using var evidence = JsonDocument.Parse(external.EvidenceJson);
            Assert.Equal(JsonValueKind.Null, evidence.RootElement.GetProperty("triggerContext").GetProperty("causation").GetProperty("rootAgentRunId").ValueKind);
            var disabled = edit with { ExpectedRevision = current.Revision, Triggers = current.Triggers!.Select(t => t.TriggerId == builtinId.ToString() ? t with { Enabled = false } : t).ToArray() };
            (await client.PutAsJsonAsync(route + "/" + automationId, disabled)).EnsureSuccessStatusCode();
            var second = Encoding.UTF8.GetBytes("{\"eventId\":\"order-2\",\"data\":{\"total\":150}}");
            Assert.Equal(ExternalEventIngressKind.Admitted, (await ingress.AdmitAsync(resource.EventKey, resource.Token, second)).Kind);
            Assert.Equal(3, (await s.GetRequiredService<ITriggerStore>().ListByDispositionAsync(OccurrenceRoutingDisposition.Pending, 20)).Count);
            var duplicate = draft with { Enabled = false, Triggers = [children[0], children[0] with { TriggerId = Guid.NewGuid().ToString() }] };
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(route, duplicate)).StatusCode);
        }
        await using var reopened = new ExperienceHost(path);
        var services = reopened.Services;
        Assert.Equal(0, await services.GetRequiredService<CoreEventDispatcher>().RunOnceAsync());
        Assert.Equal(0, await services.GetRequiredService<ExternalEventIngress>().ResumePendingAsync());
        var loaded = (await services.GetRequiredService<ITriggerStore>().GetAsync(new(instanceId, LocalUserProfile.Id), automationId))!;
        Assert.Equal(2, loaded.Triggers.Count);
        Assert.False(loaded.Triggers.Single(t => t.TriggerId == builtinId).Enabled);
        Assert.Equal(3, (await services.GetRequiredService<ITriggerStore>().ListByDispositionAsync(OccurrenceRoutingDisposition.Pending, 20)).Count);
        await services.GetRequiredService<AgentCore.Application.Triggers.TriggerOccurrenceRouter>().RouteOnceAsync();
        Assert.Equal(3, (await services.GetRequiredService<AgentCore.Application.Execution.BackgroundOccurrenceIntake>().AcceptAwaitingAsync()).Accepted);
        await UnifiedAutomationJourneyTests.Drain(services);
        using var reviewClient = TestOwnerCapability.CreateOwnerClient(reopened);
        var runPage = (await reviewClient.GetFromJsonAsync<AgentRunPageResponse>($"/api/v2/agent-instances/{instanceId}/agent-runs"))!;
        Assert.Equal(3, runPage.Items.Count);
        Assert.Single(runPage.Items, r => r.TriggerOrigin?.TriggerId == builtinId.ToString() && r.TriggerOrigin.Source?.Key == "instance.config_changed" && r.TriggerOrigin.TriggerRevision == 1);
        Assert.Equal(2, runPage.Items.Count(r => r.TriggerOrigin?.TriggerId == webhookId.ToString() && r.TriggerOrigin.Source?.Kind == "webhook"));
        var builtinRun = Assert.Single(runPage.Items, r => r.TriggerOrigin?.TriggerId == builtinId.ToString());
        await using (var db = await services.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>().CreateDbContextAsync())
        {
            var occurrence = await db.TriggerOccurrences.SingleAsync(o => o.OccurrenceId == builtinRun.SourceOccurrenceId);
            var archive = System.Text.Json.Nodes.JsonNode.Parse(occurrence.EvidenceJson)!.AsObject();
            archive.Remove("triggerId"); archive.Remove("source"); archive["padding"] = "";
            archive["triggerSummary"] = "Core Event · instance.config_changed";
            archive["padding"] = new string('x', TriggerLimits.MaxEvidenceBytes - Encoding.UTF8.GetByteCount(archive.ToJsonString()));
            occurrence.EvidenceJson = archive.ToJsonString();
            Assert.Equal(TriggerLimits.MaxEvidenceBytes, Encoding.UTF8.GetByteCount(occurrence.EvidenceJson));
            await db.SaveChangesAsync();
        }
        var historicalPage = (await reviewClient.GetFromJsonAsync<AgentRunPageResponse>($"/api/v2/agent-instances/{instanceId}/agent-runs"))!;
        var historicalRun = Assert.Single(historicalPage.Items, r => r.AgentRunId == builtinRun.AgentRunId);
        Assert.Equal(builtinRun.TriggerOrigin! with { Summary = "Core Event · instance.config_changed" }, historicalRun.TriggerOrigin);

    }
    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("[null]")]
    [InlineData("[null,null]")]
    public async Task Invalid_child_collections_are_validation_errors_without_partial_writes(string children)
    {
        await using var host = new ExperienceHost(Path.Combine(Path.GetTempPath(), $"invalid-children-{Guid.NewGuid():N}.db"));
        var instance = await host.Services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 21);
        using var client = TestOwnerCapability.CreateOwnerClient(host);
        var route = $"/api/v2/admin/agent-instances/{instance.InstanceId}/automations";
        var body = System.Text.Json.Nodes.JsonNode.Parse("{\"expectedRevision\":0,\"enabled\":false,\"name\":\"Invalid children\",\"instructions\":\"Keep valid configuration\",\"executionTarget\":{\"kind\":\"backgroundSession\"},\"completionDelivery\":{\"kind\":\"none\"}}")!;
        body["triggers"] = System.Text.Json.Nodes.JsonNode.Parse(children);
        var response = await client.PostAsJsonAsync(route, body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<AutomationReview>(route))!.Items);
    }

    private sealed class ExplicitUserAuthorizer : AgentCore.Application.Triggers.ITriggerCommandAuthorizer
    {
        public ValueTask<AgentCore.Application.Triggers.TriggerCommandAuthorizationDecision> AuthorizeCurrentTurnAsync(string? text, string? language,
            AgentCore.Application.Triggers.TriggerCommandAction action, AgentCore.Application.Triggers.ScheduleConversationContext? context = null,
            AgentCore.Application.Triggers.ScheduleDraftContext? draft = null, CancellationToken ct = default)
            => ValueTask.FromResult(AgentCore.Application.Triggers.TriggerCommandAuthorizationDecision.Allow);
        public bool IsScheduleConfirmation(string? text, string? language) => false;
    }

    [Fact(Timeout = 60000)]
    public async Task Chat_collection_authoring_retains_siblings_and_occurrence_context_cannot_write()
    {
        await using var host = new ExperienceHost(Path.Combine(Path.GetTempPath(), $"chat-children-{Guid.NewGuid():N}.db"));
        var s = host.Services;
        var instance = await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 21);
        await s.GetRequiredService<ILocalUserProfileService>().GetLocalProfileAsync();
        var owner = new TriggerOwner(instance.InstanceId, LocalUserProfile.Id);
        var definitions = s.GetRequiredService<IAgentDefinitionStore>();
        var definition = (await definitions.GetAsync("general-assistant", 21))!;
        var now = DateTimeOffset.UtcNow;
        var context = new AgentCore.Application.Triggers.TriggerCommandContext(owner, Guid.NewGuid(), "UTC", "Create this disabled automation", "en",
            AgentCore.Application.Triggers.TriggerAuthorizationClassification.CurrentUserTurn, AgentCore.Application.Triggers.TriggerCommandAction.Create, false, null, Guid.NewGuid(), now);
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        async Task<AgentCore.Application.Tools.ToolExecutionResult> Execute(string tool, object args, AgentCore.Application.Triggers.TriggerCommandContext command) =>
            await AgentCore.Application.Triggers.TriggerScheduleCommands.ExecuteAsync(definition, s.GetRequiredService<IAutomationService>(), tool,
                JsonSerializer.SerializeToElement(args), command, default, new ExplicitUserAuthorizer(), s.GetRequiredService<IAgentInstanceStore>(), definitions,
                s.GetRequiredService<IMemoryStore>(), s.GetRequiredService<AgentCore.Application.Triggers.AdminAutomationAuthoringService>());
        var created = await Execute(AgentCore.Application.Tools.ToolCatalog.AutomationCreate, new { name = "Two independent signals", instructions = "Review each signal quietly.", executionTarget = "backgroundSession",
            triggers = new[] { new { triggerId = first, revision = 1, enabled = true, kind = "event", source = new { kind = "builtin", key = "run.failed" }, filterExpression = "true" },
                new { triggerId = second, revision = 1, enabled = true, kind = "event", source = new { kind = "builtin", key = "session.ended" }, filterExpression = "false" } } }, context);
        Assert.DoesNotContain("\"error\"", created.Text);
        var store = s.GetRequiredService<ITriggerStore>(); var saved = Assert.Single(await store.ListAsync(owner, null));
        Assert.Equal(AutomationStatus.Disabled, saved.Status); Assert.Equal(2, saved.Triggers.Count);
        var updated = await Execute(AgentCore.Application.Tools.ToolCatalog.AutomationUpdate, new { automationId = saved.AutomationId, expectedRevision = saved.Revision, name = "Renamed task" },
            context with { AllowedActions = AgentCore.Application.Triggers.TriggerCommandAction.Update });
        Assert.DoesNotContain("\"error\"", updated.Text);
        saved = (await store.GetAsync(owner, saved.AutomationId))!;
        Assert.Equal(new[] { first, second }.Order(), saved.Triggers.Select(t => t.TriggerId).Order()); Assert.All(saved.Triggers, t => Assert.Equal(1, t.Revision));
        var blockedEnable = await Execute(AgentCore.Application.Tools.ToolCatalog.AutomationUpdate, new { automationId = saved.AutomationId, expectedRevision = saved.Revision, enabled = true }, context);
        Assert.Contains("error", blockedEnable.Text); Assert.Equal(AutomationStatus.Disabled, (await store.GetAsync(owner, saved.AutomationId))!.Status);
        var denied = await Execute(AgentCore.Application.Tools.ToolCatalog.AutomationUpdate, new { automationId = saved.AutomationId, expectedRevision = saved.Revision, name = "Unauthorized" },
            context with { Classification = AgentCore.Application.Triggers.TriggerAuthorizationClassification.Occurrence });
        Assert.Contains("error", denied.Text); Assert.Equal("Renamed task", (await store.GetAsync(owner, saved.AutomationId))!.Name);
    }

}
