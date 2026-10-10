using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Triggers;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed class CoreEventAutomationJourneyTests
{
    private sealed class EventClock(DateTimeOffset now) : TimeProvider
    { public override DateTimeOffset GetUtcNow() => now; public void Advance(TimeSpan interval) => now += interval; }
    [Fact(Timeout = 60000)]
    public async Task Completed_user_runs_across_sessions_are_coalesced_reviewed_and_not_replayed()
    {
        var db = Path.Combine(Path.GetTempPath(), $"core-review-{Guid.NewGuid():N}.db");
        var clock = new EventClock(DateTimeOffset.UtcNow);
        Guid instanceId; Guid[] sourceIds;
        await using (var host = new ExperienceHost(db, clock: clock))
        {
            var services = host.Services;
            var instance = await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 22);
            instanceId = instance.InstanceId;
            await services.GetRequiredService<IExperienceStore>().ConfigureAsync(instanceId, 0, true);
            using var client = TestOwnerCapability.CreateOwnerClient(host);
            var template = AutomationPresetCatalog.Templates.Single(p => p.PresetId == "review-recent-work");
            var path = $"/api/v2/admin/agent-instances/{instanceId}/automations";
            var response = await client.PostAsJsonAsync(path, new AutomationRequest(0, true, template.Name, template.Instructions,
                new AutomationTriggerDto("coreEvent", CoreEventKey: template.CoreEventKey, FilterExpression: template.FilterExpression, Dispatch: new("coalesceLatest", 60)),
                ExecutionTarget: new("backgroundSession"), CompletionDelivery: new("none"), RequiresTools: true, PresetId: template.PresetId, PresetVersion: 1));
            response.EnsureSuccessStatusCode();
            var automation = (await response.Content.ReadFromJsonAsync<AutomationResponse>())!;
            var definition = (await services.GetRequiredService<IAgentDefinitionStore>().GetAsync("general-assistant", 22))!;
            var runs = services.GetRequiredService<IAgentRunStore>();
            var owner = new AgentRunOwner(instanceId, LocalUserProfile.Id);
            sourceIds = new Guid[2];
            for (var i = 0; i < 2; i++)
            {
                var now = clock.GetUtcNow();
                var snapshot = AgentRunTestFixtures.Snapshot(owner, definition, now);
                var run = AgentRunTestFixtures.Run(snapshot, now);
                await runs.AdmitAsync(snapshot, 0, run);
                run = await runs.ApplyAsync(owner, run.AgentRunId, new AgentRunCommand.Claim(run.Revision, now, Guid.NewGuid(), now.AddMinutes(5)));
                run = await runs.ApplyAsync(owner, run.AgentRunId, new AgentRunCommand.Checkpoint(run.Revision, now, run.Claim!.Generation, new("{}", 2, 100, 120000), null));
                var answer = new ConversationEntry(Guid.NewGuid(), 2, null, ConversationRole.Assistant, "Checked observable work", run.ResponseId, EntryStatus.Completed, SessionMode.Text, 0, 23, now);
                await runs.CommitOutcomeAsync(snapshot with { Revision = 2, Entries = snapshot.Entries.Append(answer).ToArray(), LastEntrySequence = 2 }, 1, owner, run.AgentRunId,
                    new AgentRunCommand.Complete(run.Revision, now, run.Claim!.Generation, answer.Text, AgentRunOutcomeKind.Response, answer.EntryId), null);
                sourceIds[i] = run.AgentRunId;
            }
            var dispatcher = services.GetRequiredService<CoreEventDispatcher>();
            Assert.Equal(0, await dispatcher.RunOnceAsync());
            clock.Advance(TimeSpan.FromSeconds(61));
            Assert.Equal(1, await dispatcher.RunOnceAsync());
            var pending = Assert.Single(await services.GetRequiredService<ITriggerStore>().ListByDispositionAsync(AgentCore.Domain.Triggers.OccurrenceRoutingDisposition.Pending, 20));
            Assert.All(sourceIds, id => Assert.Contains(id.ToString(), pending.EvidenceJson));
            var router = services.GetRequiredService<TriggerOccurrenceRouter>();
            await router.RouteOnceAsync();
            await services.GetRequiredService<AgentCore.Application.Execution.BackgroundOccurrenceIntake>().AcceptAwaitingAsync();
            var queuedReview = Assert.Single(await runs.ListAsync(owner, 20), r => r.Admission.Activation.Kind == ActivationKind.CoreEvent);
            var sourceArguments = JsonSerializer.SerializeToElement(new { sourceKind = "AgentRun", sourceId = sourceIds[0] });
            await services.GetRequiredService<AgentCore.Application.Experience.ExperienceService>().InspectSourceAsync(instanceId, queuedReview.AgentRunId, sourceArguments, default, outputBudget: 128);
            Assert.Equal(2, (await services.GetRequiredService<ICoreEventStore>().CoveragePageAsync(new(instanceId, LocalUserProfile.Id), Guid.Parse(automation.AutomationId), null, 24)).Items.Count);
            await UnifiedAutomationJourneyTests.Drain(services);
            var reviews = (await runs.ListAsync(owner, 20)).Where(r => r.Admission.Activation.Kind == ActivationKind.CoreEvent).ToArray();
            var review = Assert.Single(reviews);
            Assert.Equal(AgentRunStatus.Completed, review.Status);
            var records = await services.GetRequiredService<IExperienceStore>().ListAsync(instanceId, 20);
            Assert.Equal(2, records.Count);
            Assert.All(sourceIds, id => Assert.Contains(records, r => r.SourceId == id));
            Assert.Empty((await services.GetRequiredService<ICoreEventStore>().CoveragePageAsync(new(instanceId, LocalUserProfile.Id), Guid.Parse(automation.AutomationId), null, 24)).Items);
            Assert.Equal(0, await dispatcher.RunOnceAsync()); // review itself fails the UserTurn filter
            // A saved preset with no new source remains an ordinary, quiet successful Run.
            (await client.PostAsJsonAsync(path + "/" + automation.AutomationId + "/run", new { expectedRevision = automation.Revision })).EnsureSuccessStatusCode();
            await UnifiedAutomationJourneyTests.Drain(services);
            var quiet = Assert.Single(await runs.ListAsync(owner, 20), r => r.Admission.Activation.Kind == ActivationKind.ManualBackground);
            Assert.Equal(AgentRunOutcomeKind.NoAction, quiet.Result!.OutcomeKind);
            Assert.Equal(2, (await services.GetRequiredService<IExperienceStore>().ListAsync(instanceId, 20)).Count);
            Assert.DoesNotContain((await services.GetRequiredService<IMemoryStore>().LoadAsync(quiet.SessionId))!.Entries, e => e.Role == ConversationRole.Assistant);
            Assert.Equal(0, await dispatcher.RunOnceAsync());
        }
        await using (var restarted = new ExperienceHost(db, clock: clock))
        {
            Assert.Equal(0, await restarted.Services.GetRequiredService<CoreEventDispatcher>().RunOnceAsync());
            Assert.Equal(2, (await restarted.Services.GetRequiredService<IExperienceStore>().ListAsync(instanceId, 20)).Count);
        }
    }

    [Fact(Timeout = 60000)]
    public async Task Webhook_true_false_and_coalescing_preserve_receipt_replay_and_owner_projection()
    {
        var clock = new EventClock(DateTimeOffset.UtcNow);
        await using var host = new ExperienceHost(Path.Combine(Path.GetTempPath(), $"webhook-filter-{Guid.NewGuid():N}.db"), clock: clock);
        var services = host.Services;
        var instance = await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 22);
        using var client = TestOwnerCapability.CreateOwnerClient(host);
        var create = await client.PostAsJsonAsync("/api/v2/admin/connections/events", new { displayName = "Orders", eventKey = "filter.orders" }); create.EnsureSuccessStatusCode();
        var source = (await create.Content.ReadFromJsonAsync<AdminWebhookEventCredentialResponse>())!;
        var path = $"/api/v2/admin/agent-instances/{instance.InstanceId}/automations";
        foreach (var (name, filter, dispatch) in new[] { ("Match", "event.data.total >= 100", "everyMatch"), ("Skip", "event.data.total < 100", "everyMatch"), ("Batch", "true", "coalesceLatest") })
            (await client.PostAsJsonAsync(path, new AutomationRequest(0, true, name, "Inspect the order", new AutomationTriggerDto("event", EventId: source.EventId, FilterExpression: filter, Dispatch: new(dispatch, dispatch == "coalesceLatest" ? 60 : null)), ExecutionTarget: new("backgroundSession"), CompletionDelivery: new("none")))).EnsureSuccessStatusCode();
        var ingress = services.GetRequiredService<ExternalEventIngress>();
        var payload = System.Text.Encoding.UTF8.GetBytes("{\"eventId\":\"order-1\",\"data\":{\"total\":150,\"agentInstanceId\":\"spoof\"}}");
        Assert.Equal(ExternalEventIngressKind.Admitted, (await ingress.AdmitAsync(source.EventKey, source.Token, payload)).Kind);
        Assert.Equal(ExternalEventIngressKind.Duplicate, (await ingress.AdmitAsync(source.EventKey, source.Token, payload)).Kind);
        // CI contention may defer a valid match. Exercise its bounded scheduler recovery
        // before asserting the final fan-out, rather than assuming immediate engine availability.
        for (var attempt = 0; attempt < EventFilterRecovery.MaxAttempts - 1 &&
            (await services.GetRequiredService<IExternalEventStore>().ListPendingDeliveriesAsync(null, 32)).Count > 0; attempt++)
        {
            clock.Advance(TimeSpan.FromSeconds(1 << attempt));
            await ingress.ResumePendingAsync();
        }
        Assert.Single(await services.GetRequiredService<ITriggerStore>().ListByDispositionAsync(AgentCore.Domain.Triggers.OccurrenceRoutingDisposition.Pending, 20));
        var activity = await services.GetRequiredService<IExternalEventStore>().ReadActivityAsync(Guid.Parse(source.EventId));
        Assert.Contains(activity.Deliveries, d => d.Status == AgentCore.Domain.Events.ExternalEventDeliveryStatus.Filtered);
        Assert.Contains(activity.Deliveries, d => d.Status == AgentCore.Domain.Events.ExternalEventDeliveryStatus.Coalesced);
        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(1, await services.GetRequiredService<CoreEventDispatcher>().RunOnceAsync());
    }

    [Fact(Timeout = 60000)]
    public async Task Committed_config_fans_out_only_to_owner_with_pinned_filter_and_replays_after_reopen()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"core-events-{Guid.NewGuid():N}.db");
        Guid instanceId;
        await using (var host = new ExperienceHost(dbPath))
        {
            var s = host.Services;
            var instances = s.GetRequiredService<AdminAgentInstanceService>();
            var a = await instances.CreateManagedAsync("general-assistant", 22);
            var b = await instances.CreateManagedAsync("general-assistant", 22);
            instanceId = a.InstanceId;
            using var client = TestOwnerCapability.CreateOwnerClient(host);
            string PathFor(Guid id) => $"/api/v2/admin/agent-instances/{id}/automations";
            AutomationRequest Draft(string name, string filter) => new(0, true, name, "Inspect state; NoAction if no useful work.",
                new AutomationTriggerDto("coreEvent", CoreEventKey: "instance.config_changed", FilterExpression: filter), ExecutionTarget: new("backgroundSession"), CompletionDelivery: new("none"));
            var yesResponse = await client.PostAsJsonAsync(PathFor(a.InstanceId), Draft("Match", "event.data.revision >= 2")); yesResponse.EnsureSuccessStatusCode();
            var yes = (await yesResponse.Content.ReadFromJsonAsync<AutomationResponse>())!;
            (await client.PostAsJsonAsync(PathFor(a.InstanceId), Draft("Skip", "false"))).EnsureSuccessStatusCode();
            (await client.PostAsJsonAsync(PathFor(b.InstanceId), Draft("Other owner", "true"))).EnsureSuccessStatusCode();
            var store = s.GetRequiredService<IAgentInstanceStore>();
            await store.UpdateWithExpectedRevisionAsync(new(a.InstanceId, a.Revision, Persona: a.Persona with { Name = "Updated Riley" }, ExpectedPersonaRevision: a.PersonaRevision), DateTimeOffset.UtcNow);
            var events = s.GetRequiredService<ICoreEventStore>();
            Assert.IsType<SqliteCoreEventStore>(events);
            await using var database = await s.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>().CreateDbContextAsync();
            Assert.Single(await database.CoreEvents.ToArrayAsync());
            var receipt = Assert.Single(await events.PendingAsync());
            Assert.Equal(a.InstanceId, receipt.Event.Owner.AgentInstanceId);
            // Pin before an edit; the saved false expression must apply only to future snapshots.
            var triggers = s.GetRequiredService<ITriggerStore>();
            var registrations = await triggers.ListAsync(receipt.Event.Owner, null);
            await events.SnapshotAsync(receipt.Event.EventId, registrations.Select(r => new AgentCore.Domain.Events.EventSubscriptionSnapshot(r.AutomationId, r.Owner, r.TriggerRevision, ((AgentCore.Domain.Triggers.CoreEventTrigger)r.Triggers.Single().Configuration).FilterExpression, new()) { TriggerId = r.Triggers.Single().TriggerId }).ToArray());
            var edit = Draft("Match", "false") with { ExpectedRevision = yes.Revision, Triggers = yes.Triggers!.Select(t => t with { FilterExpression = "false" }).ToArray() };
            (await client.PutAsJsonAsync(PathFor(a.InstanceId) + "/" + yes.AutomationId, edit)).EnsureSuccessStatusCode();
            Assert.Equal(1, await s.GetRequiredService<CoreEventDispatcher>().RunOnceAsync());
            var deliveries = await events.DeliveriesAsync(receipt.Event.EventId);
            Assert.Equal(2, deliveries.Count);
            Assert.Contains(deliveries, d => d.Status == AgentCore.Domain.Events.EventMatchStatus.Filtered);
            Assert.Equal(0, await s.GetRequiredService<CoreEventDispatcher>().RunOnceAsync());
            var occurrences = await triggers.ListByDispositionAsync(AgentCore.Domain.Triggers.OccurrenceRoutingDisposition.Pending, 20);
            Assert.Single(occurrences); Assert.Equal(a.InstanceId, occurrences[0].Owner.AgentInstanceId);
            var test = await client.PostAsJsonAsync(PathFor(a.InstanceId) + "/filter-test", new AutomationFilterTestRequest("true", JsonSerializer.SerializeToElement(new { schemaVersion = 1, data = new { } })));
            test.EnsureSuccessStatusCode(); Assert.Single(await triggers.ListByDispositionAsync(AgentCore.Domain.Triggers.OccurrenceRoutingDisposition.Pending, 20));
        }
        await using (var reopened = new ExperienceHost(dbPath))
        {
            Assert.Equal(0, await reopened.Services.GetRequiredService<CoreEventDispatcher>().RunOnceAsync());
            var deliveries = await reopened.Services.GetRequiredService<ICoreEventStore>().ActivityAsync(new(instanceId, LocalUserProfile.Id));
            Assert.Equal(2, deliveries.Count);
        }
    }
    [Fact(Timeout = 60000)]
    public async Task Adoption_is_effective_only_and_reentrant_or_unknown_filter_versions_fail_closed()
    {
        await using var host = new ExperienceHost(Path.Combine(Path.GetTempPath(), $"core-chain-{Guid.NewGuid():N}.db"));
        var services = host.Services;
        var instance = await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 21);
        var instances = services.GetRequiredService<IAgentInstanceStore>();
        var events = services.GetRequiredService<ICoreEventStore>();
        await instances.UpdateActiveVersionAsync(instance.InstanceId, 21, DateTimeOffset.UtcNow);
        Assert.Empty(await events.PendingAsync());
        await instances.UpdateActiveVersionAsync(instance.InstanceId, 22, DateTimeOffset.UtcNow);
        await instances.UpdateActiveVersionAsync(instance.InstanceId, 22, DateTimeOffset.UtcNow);
        var adopted = Assert.Single(await events.PendingAsync());
        Assert.Equal("harness.definition_adopted", adopted.Event.Key);
        Assert.Contains("\"previousVersion\":21", adopted.Event.DataJson);
        Assert.Contains("\"activeVersion\":22", adopted.Event.DataJson);
        using var client = TestOwnerCapability.CreateOwnerClient(host);
        var path = $"/api/v2/admin/agent-instances/{instance.InstanceId}/automations";
        var response = await client.PostAsJsonAsync(path, new AutomationRequest(0, true, "Chain",
            "Inspect safely", new AutomationTriggerDto("coreEvent", CoreEventKey: "run.completed", FilterExpression: "true"),
            ExecutionTarget: new("backgroundSession"), CompletionDelivery: new("none")));
        response.EnsureSuccessStatusCode();
        var automation = (await response.Content.ReadFromJsonAsync<AutomationResponse>())!;
        var automationId = Guid.Parse(automation.AutomationId);
        var owner = new AgentCore.Domain.Triggers.TriggerOwner(instance.InstanceId, LocalUserProfile.Id);
        var contexts = services.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>();
        var staged = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var id = Guid.NewGuid(); staged.Add(id);
            var source = new AgentCore.Domain.Events.CoreEventOccurrence(id, $"fixture:{id:D}", owner, "run.completed",
                DateTimeOffset.UtcNow, "{\"activationKind\":\"UserTurn\",\"outcomeKind\":\"Response\"}",
                Guid.NewGuid(), i == 0 ? 4 : 1, i == 1 ? [automationId] : []);
            await using var db = await contexts.CreateDbContextAsync();
            db.CoreEvents.Add(new CoreEventRecord { EventId = id.ToString("D"), DedupeKey = source.DedupeKey,
                AgentInstanceId = instance.InstanceId.ToString("D"), ProfileId = LocalUserProfile.Id.ToString("D"),
                ReceivedAtUtc = source.OccurredAtUtc.ToUnixTimeMilliseconds(), PayloadJson = JsonSerializer.Serialize(source) });
            await db.SaveChangesAsync();
            await events.SnapshotAsync(id, [new AgentCore.Domain.Events.EventSubscriptionSnapshot(automationId, owner, 1, "true", new())
                { TriggerId = Guid.Parse(automation.Triggers!.Single().TriggerId!), ExpressionVersion = i == 2 ? "js-expression-future" : "js-expression-v1" }]);
        }
        Assert.Equal(0, await services.GetRequiredService<CoreEventDispatcher>().RunOnceAsync());
        Assert.Empty(await services.GetRequiredService<ITriggerStore>().ListByDispositionAsync(AgentCore.Domain.Triggers.OccurrenceRoutingDisposition.Pending, 20));
        Assert.Equal(AgentCore.Domain.Events.EventMatchStatus.LoopSkipped, Assert.Single(await events.DeliveriesAsync(staged[0])).Status);
        Assert.Equal(AgentCore.Domain.Events.EventMatchStatus.LoopSkipped, Assert.Single(await events.DeliveriesAsync(staged[1])).Status);
        Assert.Equal("filter-expression-version", Assert.Single(await events.DeliveriesAsync(staged[2])).Code);
        Assert.Equal(0, await services.GetRequiredService<CoreEventDispatcher>().RunOnceAsync());
    }

    [Fact(Timeout = 60000)]
    public async Task Run_now_bypasses_core_filter_without_fabricating_a_source_event()
    {
        await using var host = new ExperienceHost(Path.Combine(Path.GetTempPath(), $"core-manual-{Guid.NewGuid():N}.db"));
        var instance = await host.Services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 22);
        using var client = TestOwnerCapability.CreateOwnerClient(host);
        var path = $"/api/v2/admin/agent-instances/{instance.InstanceId}/automations";
        var response = await client.PostAsJsonAsync(path, new AutomationRequest(0, true, "Manual",
            "Inspect current state", new AutomationTriggerDto("coreEvent", CoreEventKey: "run.completed", FilterExpression: "false"),
            ExecutionTarget: new("backgroundSession"), CompletionDelivery: new("none")));
        response.EnsureSuccessStatusCode();
        var automation = (await response.Content.ReadFromJsonAsync<AutomationResponse>())!;
        (await client.PostAsJsonAsync(path + "/" + automation.AutomationId + "/run", new { expectedRevision = automation.Revision })).EnsureSuccessStatusCode();
        var occurrence = Assert.Single(await host.Services.GetRequiredService<ITriggerStore>().ListByDispositionAsync(AgentCore.Domain.Triggers.OccurrenceRoutingDisposition.Pending, 20));
        Assert.Equal(AgentCore.Domain.Triggers.TriggerSourceKind.ManualInvocation, occurrence.SourceKind);
        Assert.Empty(await host.Services.GetRequiredService<ICoreEventStore>().PendingAsync());
    }

}
