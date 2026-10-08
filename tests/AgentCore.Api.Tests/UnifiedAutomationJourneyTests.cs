using System.Net;
using System.Net.Http.Json;
using System.Text;
using AgentCore.Application.Admin;
using AgentCore.Application.Events;
using AgentCore.Application.Experience;
using AgentCore.Application.Ports;
using AgentCore.Application.Triggers;
using AgentCore.Application.Work;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Triggers;
using AgentCore.Domain.Work;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed class UnifiedAutomationJourneyTests
{
    [Fact(Timeout = 60000)]
    public async Task Schedule_manual_run_and_event_reaction_share_completion_and_survive_restart()
    {
        var db = Path.Combine(Path.GetTempPath(), $"unified-automation-{Guid.NewGuid():N}.db");
        Guid instanceId; string scheduleId; string eventAutomationId;
        await using (var host = new ExperienceHost(db))
        {
            var s = host.Services;
            instanceId = (await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("secretary", 3)).InstanceId;
            using var client = TestOwnerCapability.CreateOwnerClient(host);
            var path = $"/api/v2/admin/agent-instances/{instanceId}/automations";
            var draft = new AutomationRequest(0, true, "Quiet review", "Inspect current state; do nothing if no work needs action.",
                new("schedule", new("fixedInterval", Interval: 3600, AnchorAtUtc: DateTimeOffset.UtcNow.AddHours(1).ToString("o"))));
            var create = await client.PostAsJsonAsync(path, draft); create.EnsureSuccessStatusCode();
            var schedule = (await create.Content.ReadFromJsonAsync<AutomationResponse>())!;
            scheduleId = schedule.AutomationId;
            Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(path + "/" + scheduleId, draft)).StatusCode);
            var mixed = draft with { Trigger = new("event", draft.Trigger.Schedule, Guid.NewGuid().ToString(), "order.placed") };
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, mixed)).StatusCode);
            var run = await client.PostAsJsonAsync(path + "/" + scheduleId + "/run", new ContinuityRevisionRequest(schedule.Revision));
            run.EnsureSuccessStatusCode();
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(path + "/" + scheduleId + "/run", new ContinuityRevisionRequest(schedule.Revision))).StatusCode);
            await Drain(s);
            var owner = new WorkOwner(instanceId, LocalUserProfile.Id);
            var work = s.GetRequiredService<IWorkItemStore>();
            var manual = Assert.Single(await work.ListAsync(owner, 20));
            Assert.Equal(WorkItemStatus.Completed, manual.Status);
            Assert.Equal("NoAction", WorkCompletionRequest.Outcome(manual.Result!.Text));
            Assert.Equal(Guid.Parse(scheduleId), manual.Provenance.AutomationId);
            Assert.Equal("Automation · Manual", manual.OriginLabel);
            Assert.Empty(await work.ListAttentionAlertKeysAsync(manual.WorkItemId));
            var sourceResponse = await client.PostAsJsonAsync("/api/v2/admin/event-sources", new { displayName = "Orders" }); sourceResponse.EnsureSuccessStatusCode();
            var source = (await sourceResponse.Content.ReadFromJsonAsync<AdminEventSourceCredentialResponse>())!;
            var reaction = draft with { Name = "Review new order", Trigger = new("event", EventSourceId: source.SourceId, EventType: "order.placed") };
            var eventResponse = await client.PostAsJsonAsync(path, reaction); eventResponse.EnsureSuccessStatusCode();
            var eventAutomation = (await eventResponse.Content.ReadFromJsonAsync<AutomationResponse>())!;
            eventAutomationId = eventAutomation.AutomationId;
            Assert.Null(eventAutomation.Trigger.Schedule);
            var ingress = s.GetRequiredService<ExternalEventIngress>();
            var body = Encoding.UTF8.GetBytes(ExternalEventEnvelope.Build("order-1", "1001"));
            Assert.Equal(ExternalEventIngressKind.Admitted, (await ingress.AdmitAsync(Guid.Parse(source.SourceKey), source.Token, body)).Kind);
            Assert.Equal(ExternalEventIngressKind.Duplicate, (await ingress.AdmitAsync(Guid.Parse(source.SourceKey), source.Token, body)).Kind);
            Assert.Single(await s.GetRequiredService<ITriggerStore>().ListByDispositionAsync(OccurrenceRoutingDisposition.Pending, 20));
            await Drain(s);
            var items = await work.ListAsync(owner, 20);
            var eventRun = Assert.Single(items, item => item.Provenance.SourceKind == WorkSourceKind.ApplicationEvent);
            Assert.Equal("NoAction", WorkCompletionRequest.Outcome(eventRun.Result!.Text));
            Assert.Contains("1001", eventRun.Provenance.EvidenceJson);
            Assert.Contains(reaction.Instructions, eventRun.Provenance.EvidenceJson);
            var review = (await client.GetFromJsonAsync<AutomationReview>(path))!;
            Assert.Equal(eventRun.WorkItemId.ToString(), Assert.Single(review.Items, item => item.AutomationId == eventAutomationId).LastWorkItemId);
            Assert.Equal(2, review.Items.Count);
        }
        await using (var restarted = new ExperienceHost(db))
        {
            using var client = TestOwnerCapability.CreateOwnerClient(restarted);
            var path = $"/api/v2/admin/agent-instances/{instanceId}/automations";
            var review = (await client.GetFromJsonAsync<AutomationReview>(path))!;
            Assert.Equal(2, review.Items.Count);
            Assert.All(review.Items, item => Assert.Equal("NoAction", item.Outcome));
            Assert.Equal("event", Assert.Single(review.Items, item => item.AutomationId == eventAutomationId).Trigger.Kind);
            Assert.Equal("schedule", Assert.Single(review.Items, item => item.AutomationId == scheduleId).Trigger.Kind);
        }
    }

    [Fact(Timeout = 60000)]
    public async Task Ordinary_automation_inspects_and_records_a_stable_owned_session_without_special_review_execution()
    {
        await using var host = new ExperienceHost(Path.Combine(Path.GetTempPath(), $"automation-review-{Guid.NewGuid():N}.db"));
        var services = host.Services;
        var instance = await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 17);
        var experience = services.GetRequiredService<ExperienceService>();
        await services.GetRequiredService<IExperienceStore>().ConfigureAsync(instance.InstanceId, 0, true);
        var source = await ExperienceJourneyTests.SeedAsync(services, instance.InstanceId);
        using var client = TestOwnerCapability.CreateOwnerClient(host);
        var path = $"/api/v2/admin/agent-instances/{instance.InstanceId}/automations";
        var request = new AutomationRequest(0, true, "Review completed Session", $"synthetic-automation-review-session: {source.SessionId}",
            new("schedule", new("daily", LocalTime: "09:00", MaxOccurrences: 3)));
        var response = await client.PostAsJsonAsync(path, request); response.EnsureSuccessStatusCode();
        var automation = (await response.Content.ReadFromJsonAsync<AutomationResponse>())!;
        (await client.PostAsJsonAsync(path + "/" + automation.AutomationId + "/run", new ContinuityRevisionRequest(automation.Revision))).EnsureSuccessStatusCode();
        await Drain(services);
        var owner = new WorkOwner(instance.InstanceId, LocalUserProfile.Id);
        var run = Assert.Single(await services.GetRequiredService<IWorkItemStore>().ListAsync(owner, 20));
        Assert.Equal("ActionCompleted", WorkCompletionRequest.Outcome(run.Result!.Text));
        var records = await services.GetRequiredService<IExperienceStore>().ListAsync(instance.InstanceId, 20);
        var record = Assert.Single(records);
        Assert.Equal(source.SessionId, record.SourceId); Assert.Equal(4, record.ThroughCursor);
        Assert.Equal(run.WorkItemId, record.GenerationWorkItemId);
        Assert.Equal(Guid.Parse(automation.AutomationId), run.Provenance.AutomationId);
        Assert.Empty(await services.GetRequiredService<IWorkItemStore>().ListAttentionAlertKeysAsync(run.WorkItemId));
        (await client.PostAsJsonAsync(path + "/" + automation.AutomationId + "/run", new ContinuityRevisionRequest(automation.Revision))).EnsureSuccessStatusCode();
        await Drain(services);
        var repeated = (await services.GetRequiredService<IWorkItemStore>().ListAsync(owner, 20)).First(item => item.WorkItemId != run.WorkItemId);
        Assert.Equal("NoAction", WorkCompletionRequest.Outcome(repeated.Result!.Text));
        Assert.Single(await services.GetRequiredService<IExperienceStore>().ListAsync(instance.InstanceId, 20));
        using var args = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(new {
            sourceKind = "Session", sourceId = source.SessionId, throughCursor = 3,
            goal = "Stale source", attempts = Array.Empty<string>(), decisions = Array.Empty<string>(), outcomes = Array.Empty<string>(),
            corrections = Array.Empty<string>(), unresolved = Array.Empty<string>(), difficulties = Array.Empty<string>(), lessons = Array.Empty<string>() }));
        var stale = await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(async () =>
            await experience.RecordAsync(instance.InstanceId, run.WorkItemId, args.RootElement, CancellationToken.None));
        Assert.Equal("Conflict", stale.Code);
    }

    [Fact]
    public void Correlated_events_are_bounded_and_preserve_the_root()
    {
        var root = Guid.NewGuid();
        string Payload(int depth) => $$$"""{"eventId":"new","type":"order.placed","rootWorkItemId":"{{{root}}}","triggerDepth":{{{depth}}},"data":{"orderReference":"1001"}}""";
        Assert.True(ExternalEventEnvelope.TryNormalize(Encoding.UTF8.GetBytes(Payload(4)), out var evidence, out _, out _, out _, out _));
        Assert.Contains(root.ToString(), evidence);
        Assert.False(ExternalEventEnvelope.TryNormalize(Encoding.UTF8.GetBytes(Payload(5)), out _, out _, out _, out _, out var reason));
        Assert.Equal("trigger_depth_exceeded", reason);
    }

    internal static async Task Drain(IServiceProvider s)
    {
        await s.GetRequiredService<TriggerOccurrenceRouter>().RouteOnceAsync();
        await s.GetRequiredService<DurableWorkIntake>().AcceptAwaitingAsync();
        await s.GetRequiredService<DurableReminderExecutor>().ExecuteDueAsync(DateTimeOffset.UtcNow, 100);
    }
}
