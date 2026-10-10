using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json;
using AgentCore.Application.Events;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Domain.Events;
using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Events;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;

public sealed class EventRecoveryOperationsTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Recovery_cursors_preserve_bounded_pages_and_timestamp_and_subscriber_ties(bool sqlite)
    {
        var path = Path.Combine(Path.GetTempPath(), $"event-recovery-ops-{Guid.NewGuid():N}.db");
        var contexts = new Factory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options);
        var memory = new InMemoryCoreEventStore();
        ICoreEventStore core = sqlite ? new SqliteCoreEventStore(contexts) : memory;
        IExternalEventStore webhook = sqlite ? new SqliteExternalEventStore(contexts) : new InMemoryExternalEventStore();
        var now = new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);
        var owner = new TriggerOwner(Guid.NewGuid(), Guid.NewGuid()); var resourceId = Guid.NewGuid();
        if (sqlite) { await using var db = await contexts.CreateDbContextAsync(); await db.Database.MigrateAsync(); }
        try
        {
            await webhook.CreateAsync(new(resourceId, "Backlog fixture", WebhookEventKind.Webhook, "backlog.fixture", "hash", WebhookEventStatus.Active, 1, now, now));
            var targets = new List<ExternalEventTarget>();
            for (var i = 40; i > 0; i--)
            {
                var id = Guid.Parse($"00000000-0000-0000-0000-{i:x12}");
                var signal = new CoreEventOccurrence(id, $"ops:{id:D}", owner, "run.completed", now.AddTicks(40 - i), "{}");
                if (sqlite) { await using var db = await contexts.CreateDbContextAsync(); CoreEventPersistence.Stage(db, signal); await db.SaveChangesAsync(); }
                else memory.Append(signal);
                var subscription = new EventSubscriptionSnapshot(id, owner, 1, null, new(EventDispatchMode.CoalesceLatest, 60), TriggerSourceKind.ApplicationEvent, resourceId);
                await core.CoalesceAsync(new(id, owner, signal.Key, now.AddTicks(40 - i), "{}"), subscription);
                targets.Add(new(id, owner.AgentInstanceId, owner.ProfileId, subscription with { SourceKind = TriggerSourceKind.ApplicationEvent, ResourceId = resourceId }));
            }
            var receiptId = Guid.NewGuid();
            await webhook.AdmitAsync(new(receiptId, resourceId, "one-receipt-many-subscribers", now, now, "{\"data\":{}}"), targets);
            var receipts = await core.PendingAsync(); Assert.Equal(32, receipts.Count);
            Assert.Equal(Enumerable.Range(1, 32).Select(i => Guid.Parse($"00000000-0000-0000-0000-{i:x12}")), receipts.Select(r => r.Event.EventId));
            var receiptCursor = new EventRecoveryCursor(receipts[^1].ReceivedAtUtc, receipts[^1].Event.EventId);
            var buckets = await core.DueBucketsAsync(now.AddMinutes(2)); Assert.Equal(32, buckets.Count);
            var bucketCursor = new EventRecoveryCursor(buckets[^1].DueAtUtc, buckets[^1].BucketId);
            var deliveries = await webhook.ListPendingDeliveriesAsync(null, 32); Assert.Equal(32, deliveries.Count);
            var deliveryCursor = new ExternalEventRecoveryCursor(deliveries[^1].EventId, deliveries[^1].AutomationId);
            if (sqlite) { core = new SqliteCoreEventStore(contexts); webhook = new SqliteExternalEventStore(contexts); }
            var nextReceipts = await core.PendingAsync(after: receiptCursor);
            var nextBuckets = await core.DueBucketsAsync(now.AddMinutes(2), after: bucketCursor);
            var nextDeliveries = await webhook.ListPendingDeliveriesAsync(receiptId, 32, after: deliveryCursor);
            Assert.Equal(8, nextReceipts.Count); Assert.Equal(8, nextBuckets.Count); Assert.Equal(8, nextDeliveries.Count);
            Assert.Equal(40, receipts.Concat(nextReceipts).Select(r => r.Event.EventId).Distinct().Count());
            Assert.Equal(40, buckets.Concat(nextBuckets).Select(b => b.BucketId).Distinct().Count());
            Assert.Equal(40, deliveries.Concat(nextDeliveries).Select(d => d.AutomationId).Distinct().Count());
            Assert.Empty(await core.PendingAsync(after: new(nextReceipts[^1].ReceivedAtUtc, nextReceipts[^1].Event.EventId)));
            Assert.Empty(await core.DueBucketsAsync(now.AddMinutes(2), after: new(nextBuckets[^1].DueAtUtc, nextBuckets[^1].BucketId)));
            Assert.Empty(await webhook.ListPendingDeliveriesAsync(null, 32, after: new(nextDeliveries[^1].EventId, nextDeliveries[^1].AutomationId)));
            // Cursors only select work; restarting/wrapping retains every unfinished item.
            Assert.Equal(32, (await core.PendingAsync()).Count);
            Assert.Equal(32, (await core.DueBucketsAsync(now.AddMinutes(2))).Count);
            Assert.Equal(32, (await webhook.ListPendingDeliveriesAsync(null, 32)).Count);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix); }
    }

    [Theory]
    [InlineData(TriggerSourceKind.CoreEvent, "filter-worker-budget", "core", "worker_budget")]
    [InlineData(TriggerSourceKind.ApplicationEvent, "filter-timeout", "webhook", "timeout")]
    public void Metrics_distinguish_exhaustion_and_count_only_completed_evaluations(TriggerSourceKind source, string code, string label, string cause)
    {
        using var metrics = new Capture();
        var snapshot = new EventSubscriptionSnapshot(Guid.NewGuid(), new(Guid.NewGuid(), Guid.NewGuid()), 1, "true", new(EventDispatchMode.EveryMatch), source);
        EventFilterResult? previous = null; var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < EventFilterRecovery.MaxAttempts; i++)
        {
            previous = EventFilterRecovery.Evaluate(new FailingFilter(code), snapshot, previous, JsonSerializer.SerializeToElement(new { }), now, default);
            Assert.Same(previous, EventFilterRecovery.Evaluate(new FailingFilter(code), snapshot, previous, JsonSerializer.SerializeToElement(new { }), now, default));
            now = now.AddSeconds(20);
        }
        var samples = metrics.Items.Where(s => s.Name == "automation_event_filter_evaluations").ToArray();
        Assert.Equal(5, samples.Length);
        Assert.Equal(4, samples.Count(s => s.Tags["outcome"] == "retry_pending"));
        Assert.Single(samples, s => s.Tags["outcome"] == "retry_exhausted");
        Assert.All(samples, sample => { Assert.Equal(label, sample.Tags["source"]); Assert.Equal(cause, sample.Tags["cause"]); });
        RuntimeTelemetry.RecordEventRecoveryPage("core", 32, 5000);
        RuntimeTelemetry.RecordEventRecoveryPage("webhook", 0, 9999);
        RuntimeTelemetry.RecordEventRecoveryPage("payload-secret-sentinel", 99, -1);
        Assert.Contains(metrics.Items, s => s.Name == "automation_event_recovery_page_size" && s.Value == 32 && s.Tags["lane"] == "core");
        Assert.Contains(metrics.Items, s => s.Name == "automation_event_recovery_oldest_age_ms" && s.Value == 5000);
        Assert.DoesNotContain(metrics.Items, s => s.Name == "automation_event_recovery_oldest_age_ms" && s.Tags["lane"] == "webhook");
        Assert.All(metrics.Items, s => Assert.All(s.Tags, tag => { Assert.Contains(tag.Key, new[] { "source", "outcome", "cause", "lane" }); Assert.DoesNotContain("secret", tag.Value); }));
    }

    [Fact]
    public async Task Concurrent_Jint_recovery_reports_pressure_without_exhaustion_after_contention_clears()
    {
        using var metrics = new Capture(); var filter = new RestrictedEventFilter();
        var snapshot = new EventSubscriptionSnapshot(Guid.NewGuid(), new(Guid.NewGuid(), Guid.NewGuid()), 1,
            "event.data['status'] === 'paid'", new(EventDispatchMode.EveryMatch));
        var envelope = JsonSerializer.SerializeToElement(new { data = new { status = "paid" } }); var now = DateTimeOffset.UtcNow;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 64).Select(_ => Task.Run(async () => { await start.Task; return EventFilterRecovery.Evaluate(filter, snapshot, null, envelope, now, default); })).ToArray();
        start.SetResult(); var results = await Task.WhenAll(tasks);
        Assert.All(results, result => Assert.True(result.Matched == true || result.Retryable, result.Code));
        foreach (var result in results.Where(r => r.Retryable))
        {
            var next = result;
            for (var attempt = 1; next.Retryable && attempt < EventFilterRecovery.MaxAttempts; attempt++)
                next = EventFilterRecovery.Evaluate(filter, snapshot, next, envelope, now.AddSeconds(20 * attempt), default);
            Assert.True(next.Matched, next.Code);
        }
        var evaluations = metrics.Items.Where(s => s.Name == "automation_event_filter_evaluations").ToArray();
        Assert.Equal(64, evaluations.Count(s => s.Tags["outcome"] == "matched"));
        Assert.DoesNotContain(evaluations, s => s.Tags["outcome"] == "retry_exhausted");
        output.WriteLine($"Synthetic concurrent Jint probe: 64 successful sources, {evaluations.Count(s => s.Tags["outcome"] == "retry_pending")} transient evaluations, zero exhausted sources.");
    }

    private sealed class FailingFilter(string code) : IEventFilterEvaluator
    { public string? Validate(string? expression) => null; public EventFilterResult Evaluate(string? expression, JsonElement envelope, CancellationToken ct = default) => new(null, "error", code); }
    private sealed class Factory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    { public AgentCoreDbContext CreateDbContext() => new(options); public Task<AgentCoreDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext()); }
    private sealed class Capture : IDisposable
    {
        public ConcurrentQueue<(string Name, double Value, Dictionary<string, string> Tags)> Items { get; } = new();
        private readonly MeterListener listener = new();
        public Capture()
        {
            listener.InstrumentPublished = (instrument, owner) =>
            { if (instrument.Meter.Name == RuntimeTelemetry.Name && instrument.Name is "automation_event_filter_evaluations" or "automation_event_recovery_page_size" or "automation_event_recovery_oldest_age_ms") owner.EnableMeasurementEvents(instrument); };
            listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Items.Enqueue((instrument.Name, value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value?.ToString() ?? ""))));
            listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Items.Enqueue((instrument.Name, value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value?.ToString() ?? ""))));
            listener.Start();
        }
        public void Dispose() => listener.Dispose();
    }
}
