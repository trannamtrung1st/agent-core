using AgentCore.Application.Execution;
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
            instanceId = (await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("secretary", 8)).InstanceId;
            using var client = TestOwnerCapability.CreateOwnerClient(host);
            var path = $"/api/v2/admin/agent-instances/{instanceId}/automations";
            var draft = new AutomationRequest(0, true, "Quiet review", "Inspect current state; do nothing if no work needs action.",
                new("schedule", new("fixedInterval", Interval: 3600, AnchorAtUtc: DateTimeOffset.UtcNow.AddHours(1).ToString("o"))), ExecutionTarget: new("backgroundSession"), CompletionDelivery: new("none"));
            var create = await client.PostAsJsonAsync(path, draft); create.EnsureSuccessStatusCode();
            var schedule = (await create.Content.ReadFromJsonAsync<AutomationResponse>())!;
            scheduleId = schedule.AutomationId;
            Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(path + "/" + scheduleId, draft)).StatusCode);
            var mixed = draft with { Trigger = new("event", draft.Trigger.Schedule, Guid.NewGuid().ToString()) };
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, mixed)).StatusCode);
            var run = await client.PostAsJsonAsync(path + "/" + scheduleId + "/run", new ContinuityRevisionRequest(schedule.Revision));
            run.EnsureSuccessStatusCode();
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(path + "/" + scheduleId + "/run", new ContinuityRevisionRequest(schedule.Revision))).StatusCode);
            await Drain(s);
            var owner = new AgentRunOwner(instanceId, LocalUserProfile.Id);
            var work = s.GetRequiredService<IAgentRunStore>();
            var manual = Assert.Single(await work.ListAsync(owner, 20));
            Assert.Equal(AgentRunStatus.Completed, manual.Status);
            Assert.Equal("NoAction", manual.Result!.OutcomeKind.ToString());
            Assert.Equal(Guid.Parse(scheduleId), (await s.SessionAsync(manual)).Origin.AutomationId);
            Assert.Equal(SessionOriginKind.AutomationOccurrence, (await s.SessionAsync(manual)).Origin.Kind);
            Assert.False(manual.Result?.AttentionRequired ?? false);
            var sourceResponse = await client.PostAsJsonAsync("/api/v2/admin/connections/events", new { displayName = "Orders", eventKey = "order.placed" }); sourceResponse.EnsureSuccessStatusCode();
            var source = (await sourceResponse.Content.ReadFromJsonAsync<AdminWebhookEventCredentialResponse>())!;
            var reaction = draft with { Name = "Review new order", Trigger = new("event", EventId: source.EventId) };
            var eventResponse = await client.PostAsJsonAsync(path, reaction); eventResponse.EnsureSuccessStatusCode();
            var eventAutomation = (await eventResponse.Content.ReadFromJsonAsync<AutomationResponse>())!;
            eventAutomationId = eventAutomation.AutomationId;
            Assert.Null(eventAutomation.Trigger.Schedule);
            var ingress = s.GetRequiredService<ExternalEventIngress>();
            var body = Encoding.UTF8.GetBytes(ExternalEventEnvelope.Build("order-1", "1001"));
            Assert.Equal(ExternalEventIngressKind.Admitted, (await ingress.AdmitAsync(source.EventKey, source.Token, body)).Kind);
            Assert.Equal(ExternalEventIngressKind.Duplicate, (await ingress.AdmitAsync(source.EventKey, source.Token, body)).Kind);
            Assert.Single(await s.GetRequiredService<ITriggerStore>().ListByDispositionAsync(OccurrenceRoutingDisposition.Pending, 20));
            await Drain(s);
            var items = await work.ListAsync(owner, 20);
            var eventRun = Assert.Single(items, item => item.Admission.Activation.Kind == ActivationKind.ApplicationEvent);
            Assert.Equal("NoAction", eventRun.Result!.OutcomeKind.ToString());
            Assert.Contains("1001", (await s.SessionAsync(eventRun)).Entries[0].Text);
            Assert.Contains(reaction.Instructions, (await s.SessionAsync(eventRun)).Entries[0].Text);
            var review = (await client.GetFromJsonAsync<AutomationReview>(path))!;
            Assert.Equal(eventRun.AgentRunId.ToString(), Assert.Single(review.Items, item => item.AutomationId == eventAutomationId).LastAgentRunId);
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
        var instance = await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 21);
        var experience = services.GetRequiredService<ExperienceService>();
        await services.GetRequiredService<IExperienceStore>().ConfigureAsync(instance.InstanceId, 0, true);
        var source = await ExperienceJourneyTests.SeedAsync(services, instance.InstanceId);
        using var client = TestOwnerCapability.CreateOwnerClient(host);
        var path = $"/api/v2/admin/agent-instances/{instance.InstanceId}/automations";
        var request = new AutomationRequest(0, true, "Review completed Session", $"synthetic-automation-review-session: {source.SessionId}",
            new("schedule", new("daily", LocalTime: "09:00", MaxOccurrences: 3)), ExecutionTarget: new("backgroundSession"), CompletionDelivery: new("none"));
        var response = await client.PostAsJsonAsync(path, request); response.EnsureSuccessStatusCode();
        var automation = (await response.Content.ReadFromJsonAsync<AutomationResponse>())!;
        (await client.PostAsJsonAsync(path + "/" + automation.AutomationId + "/run", new ContinuityRevisionRequest(automation.Revision))).EnsureSuccessStatusCode();
        await Drain(services);
        var owner = new AgentRunOwner(instance.InstanceId, LocalUserProfile.Id);
        var run = Assert.Single(await services.GetRequiredService<IAgentRunStore>().ListAsync(owner, 20));
        Assert.Equal("Response", run.Result!.OutcomeKind.ToString());
        var records = await services.GetRequiredService<IExperienceStore>().ListAsync(instance.InstanceId, 20);
        var record = Assert.Single(records);
        Assert.Equal(source.SessionId, record.SourceId); Assert.Equal(4, record.ThroughCursor);
        Assert.Equal(run.AgentRunId, record.GenerationAgentRunId);
        Assert.Equal(Guid.Parse(automation.AutomationId), (await services.SessionAsync(run)).Origin.AutomationId);
        Assert.False(run.Result?.AttentionRequired ?? false);
        (await client.PostAsJsonAsync(path + "/" + automation.AutomationId + "/run", new ContinuityRevisionRequest(automation.Revision))).EnsureSuccessStatusCode();
        await Drain(services);
        var repeated = (await services.GetRequiredService<IAgentRunStore>().ListAsync(owner, 20)).First(item => item.AgentRunId != run.AgentRunId);
        Assert.Equal("NoAction", repeated.Result!.OutcomeKind.ToString());
        Assert.Single(await services.GetRequiredService<IExperienceStore>().ListAsync(instance.InstanceId, 20));
        using var args = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(new {
            sourceKind = "Session", sourceId = source.SessionId, throughCursor = 3,
            goal = "Stale source", attempts = Array.Empty<string>(), decisions = Array.Empty<string>(), outcomes = Array.Empty<string>(),
            corrections = Array.Empty<string>(), unresolved = Array.Empty<string>(), difficulties = Array.Empty<string>(), lessons = Array.Empty<string>() }));
        var stale = await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(async () =>
            await experience.RecordAsync(instance.InstanceId, run.AgentRunId, args.RootElement, CancellationToken.None));
        Assert.Equal("Conflict", stale.Code);
    }

    [Fact]
    public void Correlated_events_are_bounded_and_preserve_the_root()
    {
        var root = Guid.NewGuid();
        string Payload(int depth) => $$$"""{"eventId":"new","rootAgentRunId":"{{{root}}}","triggerDepth":{{{depth}}},"data":{"orderReference":"1001"}}""";
        Assert.True(ExternalEventEnvelope.TryNormalize(Encoding.UTF8.GetBytes(Payload(4)), out var evidence, out _, out _, out _));
        Assert.Contains(root.ToString(), evidence);
        Assert.False(ExternalEventEnvelope.TryNormalize(Encoding.UTF8.GetBytes(Payload(5)), out _, out _, out _, out var reason));
        Assert.Equal("trigger_depth_exceeded", reason);
    }

    internal static async Task Drain(IServiceProvider s)
    {
        await s.GetRequiredService<TriggerOccurrenceRouter>().RouteOnceAsync();
        await s.GetRequiredService<BackgroundOccurrenceIntake>().AcceptAwaitingAsync();
        await s.ExecuteRunsAsync(100);
    }
}
