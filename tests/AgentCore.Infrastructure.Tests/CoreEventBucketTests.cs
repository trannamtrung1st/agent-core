using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Domain.Events;
using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;

public sealed class CoreEventBucketTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Bounded_buckets_recover_with_all_sources_and_owner_checked_coverage(bool sqlite)
    {
        var path = Path.Combine(Path.GetTempPath(), $"core-buckets-{Guid.NewGuid():N}.db");
        var contexts = new Factory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options);
        var memory = new InMemoryCoreEventStore();
        ICoreEventStore store = sqlite ? new SqliteCoreEventStore(contexts) : memory;
        var owner = new TriggerOwner(Guid.NewGuid(), Guid.NewGuid());
        var other = new TriggerOwner(owner.AgentInstanceId, Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;
        var subscription = new EventSubscriptionSnapshot(Guid.NewGuid(), owner, 1, "true", new(EventDispatchMode.CoalesceLatest, 60));
        if (sqlite) { await using var db = await contexts.CreateDbContextAsync(); await db.Database.MigrateAsync(); }
        var ids = new List<Guid>();
        try
        {
            for (var i = 0; i < 27; i++)
            {
                var sourceId = Guid.NewGuid(); ids.Add(sourceId);
                var source = new CoreEventOccurrence(Guid.NewGuid(), $"run.completed:{sourceId:D}", owner, "run.completed", now,
                    JsonSerializer.Serialize(new { agentRunId = sourceId, sessionId = Guid.NewGuid(), activationKind = "UserTurn", outcomeKind = "Response" }));
                if (sqlite) { await using var db = await contexts.CreateDbContextAsync(); CoreEventPersistence.Stage(db, source); await db.SaveChangesAsync(); }
                else memory.Append(source);
                await store.SnapshotAsync(source.EventId, [subscription, subscription with { AutomationId = Guid.NewGuid(), Owner = other }]);
                Assert.Single(await store.DeliveriesAsync(source.EventId));
                await store.DecideAsync(source.EventId, subscription.AutomationId, new(true, "matched"));
                var item = new EventBucketSource(source.EventId, owner, source.Key, now, source.DataJson);
                await store.CoalesceAsync(item, subscription);
                await store.CoalesceAsync(item, subscription); // recovery after bucket commit cannot append twice
            }
            Assert.Empty(await store.DueBucketsAsync(now.AddSeconds(59)));
            if (sqlite) store = new SqliteCoreEventStore(contexts);
            var buckets = await store.DueBucketsAsync(now.AddSeconds(61));
            Assert.Equal(27, buckets.Sum(b => b.Sources.Count)); Assert.All(buckets, b => Assert.InRange(b.Sources.Count, 1, 24));
            foreach (var b in buckets) await store.CompleteBucketAsync(b.BucketId, code: "evidence-budget");
            Assert.Empty(await store.DueBucketsAsync(now.AddSeconds(61)));
            Assert.Empty((await store.CoveragePageAsync(other, subscription.AutomationId, null, 8)).Items);
            var read = new List<EventSourceCoverage>(); string? cursor = null;
            do { var page = await store.CoveragePageAsync(owner, subscription.AutomationId, cursor, 8); read.AddRange(page.Items); cursor = page.NextCursor; } while (cursor is not null);
            Assert.Equal(27, read.Count); Assert.Equal(27, read.Select(r => r.Source.EventId).Distinct().Count());
            var first = read[0]; using var data = JsonDocument.Parse(first.Source.DataJson);
            await store.MarkSourceInspectedAsync(other, first.BucketId, data.RootElement.GetProperty("agentRunId").GetGuid());
            Assert.Equal(8, (await store.CoveragePageAsync(owner, subscription.AutomationId, null, 8)).Items.Count);
            await store.MarkSourceInspectedAsync(owner, first.BucketId, data.RootElement.GetProperty("agentRunId").GetGuid());
            Assert.DoesNotContain((await store.CoveragePageAsync(owner, subscription.AutomationId, null, 8)).Items, r => r.Source.EventId == first.Source.EventId);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix); }
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Capacity_overflow_retains_owner_checked_source_coverage_without_another_pending_bucket(bool sqlite)
    {
        var path = Path.Combine(Path.GetTempPath(), $"core-capacity-{Guid.NewGuid():N}.db");
        var contexts = new Factory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options);
        ICoreEventStore store = sqlite ? new SqliteCoreEventStore(contexts) : new InMemoryCoreEventStore();
        var now = DateTimeOffset.UtcNow;
        var owner = new TriggerOwner(Guid.NewGuid(), Guid.NewGuid());
        var subscription = new EventSubscriptionSnapshot(Guid.NewGuid(), owner, 1, null, new(EventDispatchMode.CoalesceLatest, 60), TriggerSourceKind.ApplicationEvent, Guid.NewGuid());
        if (sqlite) { await using var db = await contexts.CreateDbContextAsync(); await db.Database.MigrateAsync(); }
        try
        {
            for (var i = 0; i < 33; i++)
                await store.CoalesceAsync(new(Guid.NewGuid(), owner, "orders", now, JsonSerializer.Serialize(new { body = new string('a', 4800) })), subscription);
            Assert.Equal(32, (await store.DueBucketsAsync(now.AddSeconds(61))).Count);
            var overflow = Assert.Single((await store.CoveragePageAsync(owner, subscription.AutomationId, null, 24)).Items);
            Assert.Equal("bucket-capacity", overflow.CompletionCode);
            Assert.Empty((await store.CoveragePageAsync(new(owner.AgentInstanceId, Guid.NewGuid()), subscription.AutomationId, null, 24)).Items);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Late_worker_uses_first_durable_filter_decision_for_core_and_webhook(bool sqlite)
    {
        var path = Path.Combine(Path.GetTempPath(), $"core-decision-{Guid.NewGuid():N}.db");
        var contexts = new Factory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options);
        var memory = new InMemoryCoreEventStore();
        ICoreEventStore core = sqlite ? new SqliteCoreEventStore(contexts) : memory;
        IExternalEventStore webhook = sqlite ? new SqliteExternalEventStore(contexts) : new InMemoryExternalEventStore();
        var now = DateTimeOffset.UtcNow;
        var owner = new TriggerOwner(Guid.NewGuid(), Guid.NewGuid());
        var automationId = Guid.NewGuid();
        var source = new CoreEventOccurrence(Guid.NewGuid(), "decision-fixture", owner, "run.completed", now, "{}");
        if (sqlite) { await using var db = await contexts.CreateDbContextAsync(); await db.Database.MigrateAsync(); CoreEventPersistence.Stage(db, source); await db.SaveChangesAsync(); }
        else memory.Append(source);
        try
        {
            await core.SnapshotAsync(source.EventId, [new(automationId, owner, 1, "true", new())]);
            var first = new EventFilterResult(null, "error", "filter-result-not-boolean");
            Assert.Equal(first, await core.DecideAsync(source.EventId, automationId, first));
            Assert.Equal(first, await core.DecideAsync(source.EventId, automationId, new(true, "matched")));
            Assert.Equal(EventMatchStatus.FilterError, Assert.Single(await core.DeliveriesAsync(source.EventId)).Status);
            var resource = new WebhookEvent(Guid.NewGuid(), "Decision fixture", WebhookEventKind.Webhook, "decision.fixture",
                "safe-fixture-hash", WebhookEventStatus.Active, 1, now, now);
            await webhook.CreateAsync(resource);
            var receipt = new ExternalEvent(Guid.NewGuid(), resource.ResourceId, "fixture-1", now, now, "{}");
            await webhook.AdmitAsync(receipt, [new(automationId, owner.AgentInstanceId, owner.ProfileId)]);
            Assert.Equal(first, await webhook.DecideDeliveryAsync(receipt.EventId, automationId, first));
            Assert.Equal(first, await webhook.DecideDeliveryAsync(receipt.EventId, automationId, new(true, "matched")));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Source_acknowledgment_ignores_non_string_webhook_run_ids(bool sqlite)
    {
        var path = Path.Combine(Path.GetTempPath(), $"core-ack-{Guid.NewGuid():N}.db");
        var contexts = new Factory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options);
        ICoreEventStore store = sqlite ? new SqliteCoreEventStore(contexts) : new InMemoryCoreEventStore();
        var now = DateTimeOffset.UtcNow;
        var owner = new TriggerOwner(Guid.NewGuid(), Guid.NewGuid());
        var subscription = new EventSubscriptionSnapshot(Guid.NewGuid(), owner, 1, null, new(EventDispatchMode.CoalesceLatest, 60), TriggerSourceKind.ApplicationEvent, Guid.NewGuid());
        var sourceId = Guid.NewGuid();
        var reviewedEventId = Guid.NewGuid();
        if (sqlite) { await using var db = await contexts.CreateDbContextAsync(); await db.Database.MigrateAsync(); }
        try
        {
            foreach (var json in new[] { "{\"agentRunId\":123}", "{\"agentRunId\":null}", "{\"agentRunId\":{}}", "{\"agentRunId\":[]}", "{\"agentRunId\":\"invalid\"}" })
                await store.CoalesceAsync(new(Guid.NewGuid(), owner, "orders", now, json), subscription);
            await store.CoalesceAsync(new(reviewedEventId, owner, "orders", now, JsonSerializer.Serialize(new { agentRunId = sourceId })), subscription);
            var bucket = Assert.Single(await store.DueBucketsAsync(now.AddSeconds(61)));
            await store.CompleteBucketAsync(bucket.BucketId);
            await store.MarkSourceInspectedAsync(owner, bucket.BucketId, sourceId);
            var remaining = (await store.CoveragePageAsync(owner, subscription.AutomationId, null, 24)).Items;
            Assert.Equal(5, remaining.Count);
            Assert.DoesNotContain(remaining, item => item.Source.EventId == reviewedEventId);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Repeated_bucket_completion_preserves_first_durable_outcome(bool sqlite)
    {
        var path = Path.Combine(Path.GetTempPath(), $"core-completion-{Guid.NewGuid():N}.db");
        var contexts = new Factory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options);
        ICoreEventStore store = sqlite ? new SqliteCoreEventStore(contexts) : new InMemoryCoreEventStore();
        var now = DateTimeOffset.UtcNow;
        var owner = new TriggerOwner(Guid.NewGuid(), Guid.NewGuid());
        var subscription = new EventSubscriptionSnapshot(Guid.NewGuid(), owner, 1, null, new(EventDispatchMode.CoalesceLatest, 60), TriggerSourceKind.ApplicationEvent, Guid.NewGuid());
        if (sqlite) { await using var db = await contexts.CreateDbContextAsync(); await db.Database.MigrateAsync(); }
        try
        {
            await store.CoalesceAsync(new(Guid.NewGuid(), owner, "orders", now, "{}"), subscription);
            var bucket = Assert.Single(await store.DueBucketsAsync(now.AddSeconds(61)));
            await store.CompleteBucketAsync(bucket.BucketId, code: "policy-denied");
            await store.CompleteBucketAsync(bucket.BucketId);
            await store.CompleteBucketAsync(bucket.BucketId, code: "model-unavailable");
            Assert.Empty(await store.DueBucketsAsync(now.AddSeconds(61)));
            var coverage = Assert.Single((await store.CoveragePageAsync(owner, subscription.AutomationId, null, 24)).Items);
            Assert.Equal("policy-denied", coverage.CompletionCode);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix); }
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task Bucket_budget_includes_causation_and_retains_oversize_source_coverage(bool sqlite, bool oversize)
    {
        var path = Path.Combine(Path.GetTempPath(), $"core-causal-budget-{Guid.NewGuid():N}.db");
        var contexts = new Factory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options);
        ICoreEventStore store = sqlite ? new SqliteCoreEventStore(contexts) : new InMemoryCoreEventStore();
        var now = DateTimeOffset.UtcNow;
        var owner = new TriggerOwner(Guid.NewGuid(), Guid.NewGuid());
        var subscription = new EventSubscriptionSnapshot(Guid.NewGuid(), owner, 1, null, new(EventDispatchMode.CoalesceLatest, 60), TriggerSourceKind.ApplicationEvent, Guid.NewGuid());
        var visited = Enumerable.Range(0, oversize ? 180 : 50).Select(_ => Guid.NewGuid()).ToArray();
        if (sqlite) { await using var db = await contexts.CreateDbContextAsync(); await db.Database.MigrateAsync(); }
        try
        {
            for (var i = 0; i < (oversize ? 1 : 3); i++)
                await store.CoalesceAsync(new(Guid.NewGuid(), owner, "orders", now, "{}", Guid.NewGuid(), 2, visited), subscription);
            var buckets = await store.DueBucketsAsync(now.AddSeconds(61));
            if (oversize)
            {
                Assert.Empty(buckets);
                var coverage = Assert.Single((await store.CoveragePageAsync(owner, subscription.AutomationId, null, 24)).Items);
                Assert.Equal("evidence-budget", coverage.CompletionCode);
                Assert.Equal(visited, coverage.Source.VisitedAutomationIds);
            }
            else
            {
                Assert.Equal(2, buckets.Count);
                Assert.Equal(3, buckets.Sum(b => b.Sources.Count));
                Assert.All(buckets.SelectMany(b => b.Sources), source => Assert.Equal(visited, source.VisitedAutomationIds));
            }
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix); }
    }


    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Retry_decision_CAS_survives_reopen_and_late_attempts_cannot_replace_the_winner(bool sqlite, bool legacy)
    {
        var path = Path.Combine(Path.GetTempPath(), $"core-retry-{Guid.NewGuid():N}.db");
        var contexts = new Factory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options);
        var memory = new InMemoryCoreEventStore();
        ICoreEventStore core = sqlite ? new SqliteCoreEventStore(contexts) : memory;
        IExternalEventStore webhook = sqlite ? new SqliteExternalEventStore(contexts) : new InMemoryExternalEventStore();
        var owner = new TriggerOwner(Guid.NewGuid(), Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;
        var snapshot = new EventSubscriptionSnapshot(Guid.NewGuid(), owner, 1, "true", new());
        var signal = new CoreEventOccurrence(Guid.NewGuid(), "retry-test", owner, "run.completed", now, "{}");
        try
        {
            if (sqlite) { await using var db = await contexts.CreateDbContextAsync(); await db.Database.MigrateAsync(); CoreEventPersistence.Stage(db, signal); await db.SaveChangesAsync(); }
            else memory.Append(signal);
            await core.SnapshotAsync(signal.EventId, [snapshot]);
            var ingress = new ExternalEvent(Guid.NewGuid(), Guid.NewGuid(), "retry-test", now, now, "{}");
            await webhook.AdmitAsync(ingress, [new(snapshot.AutomationId, owner.AgentInstanceId, owner.ProfileId, snapshot)]);
            var retry = legacy ? new EventFilterResult(null, "error", "filter-worker-budget") : new EventFilterResult(null, "retryPending", "filter-worker-budget", RetryAtUtc: now.AddSeconds(1));
            Assert.Equal(retry, await core.DecideAsync(signal.EventId, snapshot.AutomationId, retry));
            Assert.Equal(retry, await webhook.DecideDeliveryAsync(ingress.EventId, snapshot.AutomationId, retry));
            if (legacy)
            {
                await core.FinishAsync(signal.EventId, snapshot.AutomationId, EventMatchStatus.FilterError, retry.Code);
                await webhook.MarkDeliveryAsync(ingress.EventId, snapshot.AutomationId, ExternalEventDeliveryStatus.FilterError);
                if (sqlite)
                {
                    await using var db = await contexts.CreateDbContextAsync();
                    const string oldJson = "{\"matched\":null,\"status\":\"error\",\"code\":\"filter-worker-budget\",\"schemaVersion\":1,\"expressionVersion\":\"js-expression-v1\"}";
                    (await db.CoreEventDeliveries.SingleAsync()).DecisionJson = oldJson;
                    (await db.ExternalEventDeliveries.SingleAsync()).DecisionJson = oldJson;
                    await db.SaveChangesAsync();
                }
            }
            if (sqlite) { core = new SqliteCoreEventStore(contexts); webhook = new SqliteExternalEventStore(contexts); }
            Assert.Single(await core.PendingAsync());
            Assert.Equal(legacy ? EventMatchStatus.FilterError : EventMatchStatus.Pending, Assert.Single(await core.DeliveriesAsync(signal.EventId)).Status);
            Assert.Equal(retry, Assert.Single(await webhook.ListPendingDeliveriesAsync(ingress.EventId, 32)).Decision);
            var matched = new EventFilterResult(true, "matched", Attempt: 2);
            Assert.Equal(matched, await core.DecideAsync(signal.EventId, snapshot.AutomationId, matched, expectedDecision: retry));
            Assert.Equal(matched, await webhook.DecideDeliveryAsync(ingress.EventId, snapshot.AutomationId, matched, expectedDecision: retry));
            // Concurrent evaluators may finish after a newer attempt; final decisions stay immutable.
            Assert.Equal(matched, await core.DecideAsync(signal.EventId, snapshot.AutomationId, retry, expectedDecision: retry));
            Assert.Equal(matched, await webhook.DecideDeliveryAsync(ingress.EventId, snapshot.AutomationId, retry, expectedDecision: retry));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix); }
    }

    private sealed class Factory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    { public AgentCoreDbContext CreateDbContext() => new(options); public Task<AgentCoreDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext()); }
}
