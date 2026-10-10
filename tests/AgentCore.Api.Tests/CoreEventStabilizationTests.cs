using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Events;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Triggers;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Events;
using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Events;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed class CoreEventStabilizationTests
{
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance() => now = now.AddSeconds(20);
    }
    private sealed class ControlledFilter : IEventFilterEvaluator
    {
        public string? Failure { get; set; }
        public bool Throws { get; set; }
        public int Calls { get; private set; }
        private readonly RestrictedEventFilter real = new();
        public string? Validate(string? expression) => real.Validate(expression);
        public EventFilterResult Evaluate(string? expression, JsonElement envelope, CancellationToken ct = default)
        {
            Calls++;
            if (expression == "true") return new(true, "matched");
            if (expression == "event.data.revision >= 2")
            {
                if (Throws) throw new InvalidOperationException("Unavailable subscriber dependency");
                if (Failure is not null) return new(null, "error", Failure);
            }
            return real.Evaluate(expression, envelope, ct);
        }
    }
    private static string Database() => Path.Combine(Path.GetTempPath(), $"core-stabilization-{Guid.NewGuid():N}.db");
    private static string PathFor(Guid instance) => $"/api/v2/admin/agent-instances/{instance}/automations";
    private static AutomationRequest Request(string expression, bool enabled = true, string dispatch = "everyMatch") =>
        new(0, enabled, "Inspect configuration", "Inspect current configuration and finish quietly.",
            new("coreEvent", CoreEventKey: "instance.config_changed", FilterExpression: expression,
                Dispatch: new(dispatch, dispatch == "coalesceLatest" ? 60 : null)),
            ExecutionTarget: new("backgroundSession"), CompletionDelivery: new("none"));
    private static async Task<AutomationResponse> Save(HttpClient client, string path, AutomationRequest request)
    {
        var response = await client.PostAsJsonAsync(path, request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AutomationResponse>())!;
    }
    private static async Task Emit(IServiceProvider services, Guid instance)
    {
        var instances = services.GetRequiredService<IAgentInstanceStore>();
        var row = (await instances.FindAsync(instance))!;
        await instances.UpdateWithExpectedRevisionAsync(new(instance, row.Revision,
            Persona: row.Persona with { Name = "Updated name" }, ExpectedPersonaRevision: row.PersonaRevision), DateTimeOffset.UtcNow);
    }

    [Theory(Timeout = 60000)]
    [InlineData("filter-timeout")]
    [InlineData("filter-worker-budget")]
    public async Task Transient_filter_recovers_after_restart_using_original_snapshot(string failure)
    {
        var db = Database(); var clock = new Clock(); var filter = new ControlledFilter { Failure = failure };
        Guid eventId; Guid automationId;
        await using (var host = new ExperienceHost(db, clock: clock, configure: s => { s.RemoveAll<IEventFilterEvaluator>(); s.AddSingleton<IEventFilterEvaluator>(filter); }))
        {
            var s = host.Services;
            var instance = await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 22);
            using var client = TestOwnerCapability.CreateOwnerClient(host);
            var saved = await Save(client, PathFor(instance.InstanceId), Request("event.data.revision >= 2"));
            automationId = Guid.Parse(saved.AutomationId);
            await Emit(s, instance.InstanceId);
            var events = s.GetRequiredService<ICoreEventStore>();
            eventId = Assert.Single(await events.PendingAsync()).Event.EventId;
            Assert.Equal(0, await s.GetRequiredService<CoreEventDispatcher>().RunOnceAsync());
            var delivery = Assert.Single(await events.DeliveriesAsync(eventId));
            Assert.Equal(EventMatchStatus.Pending, delivery.Status);
            Assert.True(delivery.Decision!.Retryable);
            Assert.Equal(1, delivery.Decision.Attempt);
            Assert.Equal(0, await s.GetRequiredService<CoreEventDispatcher>().RunOnceAsync());
            Assert.Equal(1, filter.Calls); // backoff does not spend another evaluation
            var edit = await client.PutAsJsonAsync(PathFor(instance.InstanceId) + "/" + saved.AutomationId,
                Request("false") with { ExpectedRevision = saved.Revision });
            edit.EnsureSuccessStatusCode();
        }
        clock.Advance();
        await using (var reopened = new ExperienceHost(db, clock: clock))
        {
            var s = reopened.Services;
            Assert.Equal(1, await s.GetRequiredService<CoreEventDispatcher>().RunOnceAsync());
            var delivery = Assert.Single(await s.GetRequiredService<ICoreEventStore>().DeliveriesAsync(eventId));
            Assert.Equal(EventMatchStatus.Admitted, delivery.Status);
            Assert.Equal("event.data.revision >= 2", delivery.Subscription.FilterExpression);
            Assert.Equal(2, delivery.Decision!.Attempt);
            Assert.Equal(automationId, Assert.Single(await s.GetRequiredService<ITriggerStore>().ListByDispositionAsync(OccurrenceRoutingDisposition.Pending, 20)).AutomationId);
            Assert.Equal(0, await s.GetRequiredService<CoreEventDispatcher>().RunOnceAsync());
            await s.GetRequiredService<TriggerOccurrenceRouter>().RouteOnceAsync();
            Assert.Equal(1, (await s.GetRequiredService<BackgroundOccurrenceIntake>().AcceptAwaitingAsync()).Accepted);
            await UnifiedAutomationJourneyTests.Drain(s);
            Assert.Equal(AgentRunStatus.Completed, Assert.Single(await s.GetRequiredService<IAgentRunStore>().ListAsync(new(delivery.Subscription.Owner.AgentInstanceId, LocalUserProfile.Id), 20)).Status);
        }
    }

    [Fact(Timeout = 60000)]
    public async Task Unavailable_subscription_does_not_block_sibling_and_recovers_without_duplicate_admission()
    {
        var filter = new ControlledFilter { Throws = true };
        await using var host = new ExperienceHost(Database(), configure: s => { s.RemoveAll<IEventFilterEvaluator>(); s.AddSingleton<IEventFilterEvaluator>(filter); });
        var s = host.Services;
        var instance = await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 22);
        using var client = TestOwnerCapability.CreateOwnerClient(host);
        await Save(client, PathFor(instance.InstanceId), Request("event.data.revision >= 2"));
        await Save(client, PathFor(instance.InstanceId), Request("true"));
        await Emit(s, instance.InstanceId);
        var eventId = Assert.Single(await s.GetRequiredService<ICoreEventStore>().PendingAsync()).Event.EventId;
        var dispatcher = s.GetRequiredService<CoreEventDispatcher>();
        Assert.Equal(1, await dispatcher.RunOnceAsync());
        Assert.Contains(await s.GetRequiredService<ICoreEventStore>().DeliveriesAsync(eventId), d => d.Status == EventMatchStatus.Pending && d.Decision is null);
        Assert.Equal(0, await dispatcher.RunOnceAsync()); // persistent failure still does not throw
        filter.Throws = false;
        Assert.Equal(1, await dispatcher.RunOnceAsync());
        Assert.Equal(2, (await s.GetRequiredService<ITriggerStore>().ListByDispositionAsync(OccurrenceRoutingDisposition.Pending, 20)).Count);
        Assert.Equal(0, await dispatcher.RunOnceAsync());
    }

    [Fact(Timeout = 60000)]
    public async Task Transient_filter_retries_are_bounded_and_permanent_errors_are_terminal()
    {
        var clock = new Clock(); var filter = new ControlledFilter { Failure = "filter-timeout" };
        await using var host = new ExperienceHost(Database(), clock: clock, configure: s => { s.RemoveAll<IEventFilterEvaluator>(); s.AddSingleton<IEventFilterEvaluator>(filter); });
        var s = host.Services;
        var instance = await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 22);
        using var client = TestOwnerCapability.CreateOwnerClient(host);
        await Save(client, PathFor(instance.InstanceId), Request("event.data.revision >= 2"));
        await Save(client, PathFor(instance.InstanceId), Request("event.data.revision"));
        await Emit(s, instance.InstanceId);
        var events = s.GetRequiredService<ICoreEventStore>(); var id = Assert.Single(await events.PendingAsync()).Event.EventId;
        for (var i = 0; i < EventFilterRecovery.MaxAttempts; i++)
        { Assert.Equal(0, await s.GetRequiredService<CoreEventDispatcher>().RunOnceAsync()); clock.Advance(); }
        var deliveries = await events.DeliveriesAsync(id);
        Assert.All(deliveries, d => Assert.Equal(EventMatchStatus.FilterError, d.Status));
        Assert.Contains(deliveries, d => d.Code == "filter-retry-exhausted" && d.Decision!.Attempt == EventFilterRecovery.MaxAttempts);
        Assert.Contains(deliveries, d => d.Code == "filter-result-not-boolean" && d.Decision!.Attempt == 1);
        Assert.Empty(await events.PendingAsync());
    }

    [Fact(Timeout = 60000)]
    public async Task Disabled_core_draft_saves_without_policy_but_enable_and_run_still_require_authority()
    {
        await using var host = new ExperienceHost(Database()); var s = host.Services;
        var instance = await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 21);
        using var client = TestOwnerCapability.CreateOwnerClient(host); var path = PathFor(instance.InstanceId);
        var saved = await Save(client, path, Request("true", false));
        var edited = await client.PutAsJsonAsync(path + "/" + saved.AutomationId, Request("false", false) with { ExpectedRevision = saved.Revision });
        edited.EnsureSuccessStatusCode(); saved = (await edited.Content.ReadFromJsonAsync<AutomationResponse>())!;
        var enable = await client.PutAsJsonAsync(path + "/" + saved.AutomationId, Request("false") with { ExpectedRevision = saved.Revision });
        Assert.Equal(HttpStatusCode.Forbidden, enable.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path + "/" + saved.AutomationId + "/run", new { expectedRevision = saved.Revision })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, Request("event.data.constructor", false))).StatusCode);
        await Emit(s, instance.InstanceId);
        Assert.Equal(0, await s.GetRequiredService<CoreEventDispatcher>().RunOnceAsync());
        Assert.Empty(await s.GetRequiredService<ITriggerStore>().ListByDispositionAsync(OccurrenceRoutingDisposition.Pending, 20));
    }

    [Fact(Timeout = 60000)]
    public async Task Customized_preset_runs_without_original_experience_prerequisite_or_forced_tools()
    {
        await using var host = new ExperienceHost(Database()); var s = host.Services;
        var instance = await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 22);
        using var client = TestOwnerCapability.CreateOwnerClient(host); var path = PathFor(instance.InstanceId);
        var saved = await Save(client, path, Request("true", false) with { RequiresTools = true, PresetId = "review-recent-work", PresetVersion = 1 });
        var edited = await client.PutAsJsonAsync(path + "/" + saved.AutomationId,
            Request("true") with { ExpectedRevision = saved.Revision, RequiresTools = false });
        edited.EnsureSuccessStatusCode(); saved = (await edited.Content.ReadFromJsonAsync<AutomationResponse>())!;
        Assert.False(saved.RequiresTools); Assert.Equal("review-recent-work", saved.PresetId);
        await Emit(s, instance.InstanceId);
        Assert.Equal(1, await s.GetRequiredService<CoreEventDispatcher>().RunOnceAsync());
        await s.GetRequiredService<TriggerOccurrenceRouter>().RouteOnceAsync();
        Assert.Equal(1, (await s.GetRequiredService<BackgroundOccurrenceIntake>().AcceptAwaitingAsync()).Accepted);
        await UnifiedAutomationJourneyTests.Drain(s);
        Assert.Equal(AgentRunStatus.Completed, Assert.Single(await s.GetRequiredService<IAgentRunStore>().ListAsync(new(instance.InstanceId, LocalUserProfile.Id), 20)).Status);
        // Provenance must not prevent manual execution either.
        (await client.PostAsJsonAsync(path + "/" + saved.AutomationId + "/run", new { expectedRevision = saved.Revision })).EnsureSuccessStatusCode();
    }

    [Fact(Timeout = 60000)]
    public async Task Webhook_transient_delivery_stays_pending_until_recovery_while_sibling_is_admitted()
    {
        var filter = new ControlledFilter { Failure = "filter-timeout" }; var clock = new Clock();
        await using var host = new ExperienceHost(Database(), clock: clock, configure: s => { s.RemoveAll<IEventFilterEvaluator>(); s.AddSingleton<IEventFilterEvaluator>(filter); });
        var s = host.Services;
        var instance = await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 22);
        using var client = TestOwnerCapability.CreateOwnerClient(host);
        var created = await client.PostAsJsonAsync("/api/v2/admin/connections/events", new { displayName = "Retry fixture", eventKey = "retry.fixture" });
        created.EnsureSuccessStatusCode(); var source = (await created.Content.ReadFromJsonAsync<AdminWebhookEventCredentialResponse>())!;
        foreach (var expression in new[] { "event.data.revision >= 2", "true" })
            await Save(client, PathFor(instance.InstanceId), Request(expression) with { Trigger = new("event", EventId: source.EventId, FilterExpression: expression) });
        var ingress = s.GetRequiredService<ExternalEventIngress>();
        var result = await ingress.AdmitAsync(source.EventKey, source.Token, System.Text.Encoding.UTF8.GetBytes("{\"eventId\":\"retry-source\",\"data\":{\"revision\":3}}"));
        Assert.Equal(ExternalEventIngressKind.Admitted, result.Kind);
        var events = s.GetRequiredService<IExternalEventStore>();
        var pending = Assert.Single(await events.ListPendingDeliveriesAsync(result.EventId, 32));
        Assert.True(pending.Decision!.Retryable);
        Assert.Equal(0, await ingress.ResumePendingAsync());
        filter.Failure = null; clock.Advance();
        Assert.Equal(1, await ingress.ResumePendingAsync());
        Assert.Empty(await events.ListPendingDeliveriesAsync(result.EventId, 32));
        Assert.Equal(2, (await s.GetRequiredService<ITriggerStore>().ListByDispositionAsync(OccurrenceRoutingDisposition.Pending, 20)).Count);
        Assert.Equal(0, await ingress.ResumePendingAsync());
    }

    [Fact(Timeout = 60000)]
    public async Task Malformed_bucket_does_not_block_other_buckets_and_remains_recoverable()
    {
        var clock = new Clock();
        await using var host = new ExperienceHost(Database(), clock: clock); var s = host.Services;
        var instance = await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 22);
        using var client = TestOwnerCapability.CreateOwnerClient(host);
        for (var i = 0; i < 2; i++) await Save(client, PathFor(instance.InstanceId), Request("true", dispatch: "coalesceLatest"));
        await Emit(s, instance.InstanceId);
        var dispatcher = s.GetRequiredService<CoreEventDispatcher>();
        Assert.Equal(0, await dispatcher.RunOnceAsync());
        var contexts = s.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>();
        Guid bucketId; string original;
        await using (var db = await contexts.CreateDbContextAsync())
        {
            var row = (await db.CoreEventBuckets.OrderBy(b => b.DueAtUtc).ToArrayAsync())[0];
            original = row.PayloadJson;
            var bucket = JsonSerializer.Deserialize<CoreEventBucket>(original, CoreEventPersistence.Json)!;
            bucketId = bucket.BucketId;
            row.PayloadJson = JsonSerializer.Serialize(bucket with { Sources = [bucket.Sources[0] with { DataJson = "{broken" }] }, CoreEventPersistence.Json);
            await db.SaveChangesAsync();
        }
        for (var i = 0; i < 4; i++) clock.Advance();
        Assert.Equal(1, await dispatcher.RunOnceAsync());
        Assert.Equal(bucketId, Assert.Single(await s.GetRequiredService<ICoreEventStore>().DueBucketsAsync(clock.GetUtcNow())).BucketId);
        await using (var db = await contexts.CreateDbContextAsync())
        {
            var row = await db.CoreEventBuckets.SingleAsync(b => b.BucketId == bucketId.ToString("D"));
            row.PayloadJson = original; await db.SaveChangesAsync();
        }
        Assert.Equal(1, await dispatcher.RunOnceAsync());
        Assert.Equal(0, await dispatcher.RunOnceAsync());
    }

    [Fact(Timeout = 60000)]
    public async Task Customized_preset_schedule_admits_without_original_prerequisites()
    {
        var clock = new Clock();
        await using var host = new ExperienceHost(Database(), clock: clock); var s = host.Services;
        var instance = await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 22);
        using var client = TestOwnerCapability.CreateOwnerClient(host);
        var path = PathFor(instance.InstanceId);
        var draft = Request("true", false) with { PresetId = "review-recent-work", PresetVersion = 1 };
        var saved = await Save(client, path, draft);
        var scheduled = Request("true") with { ExpectedRevision = saved.Revision,
            Trigger = new("schedule", new("oneShot", AtUtc: clock.GetUtcNow().AddSeconds(30).ToString("O"))) };
        var edited = await client.PutAsJsonAsync(path + "/" + saved.AutomationId, scheduled); edited.EnsureSuccessStatusCode();
        clock.Advance(); clock.Advance();
        var pass = await s.GetRequiredService<TriggerScheduler>().RunOnceAsync(clock.GetUtcNow());
        Assert.Equal(1, pass.Admitted);
        await s.GetRequiredService<TriggerOccurrenceRouter>().RouteOnceAsync();
        Assert.Equal(1, (await s.GetRequiredService<BackgroundOccurrenceIntake>().AcceptAwaitingAsync()).Accepted);
    }
}
