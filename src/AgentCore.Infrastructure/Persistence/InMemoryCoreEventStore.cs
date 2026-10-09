using AgentCore.Application.Ports;
using AgentCore.Domain.Events;
using AgentCore.Domain.Triggers;

namespace AgentCore.Infrastructure.Persistence;

public sealed class InMemoryCoreEventStore : ICoreEventStore
{
    private readonly Lock gate = new();
    private readonly Dictionary<Guid, CoreEventReceipt> receipts = [];
    private readonly Dictionary<(Guid, Guid), CoreEventDelivery> deliveries = [];
    private readonly HashSet<(Guid BucketId, Guid SourceId)> inspected = [];
    public ValueTask<EventCoveragePage> CoveragePageAsync(TriggerOwner owner, Guid automationId, string? cursor, int limit, CancellationToken ct = default)
    { lock (gate) return ValueTask.FromResult(EventCoverage.Page(buckets.Values.Where(b => b.Flushed && b.Subscription.AutomationId == automationId).OrderBy(b => b.BucketId.ToString("D"), StringComparer.Ordinal).Select(b => (b, (IReadOnlyCollection<Guid>)b.Sources.Where(e => inspected.Contains((b.BucketId, e.EventId))).Select(e => e.EventId).ToArray())), owner, cursor, limit, false)); }
    public ValueTask MarkSourceInspectedAsync(TriggerOwner owner, Guid bucketId, Guid sourceId, CancellationToken ct = default)
    {
        lock (gate) { if (buckets.TryGetValue(bucketId, out var b) && b.Subscription.Owner == owner)
            foreach (var prior in buckets.Values.Where(prior => prior.Subscription.Owner == owner && prior.Subscription.AutomationId == b.Subscription.AutomationId))
                foreach (var e in prior.Sources.Where(e => SourceId(e) == sourceId)) inspected.Add((prior.BucketId, e.EventId)); }
        return ValueTask.CompletedTask;
    }
    private static Guid? SourceId(EventBucketSource e) { using var d = System.Text.Json.JsonDocument.Parse(e.DataJson); return d.RootElement.TryGetProperty("agentRunId", out var id) && id.TryGetGuid(out var g) ? g : null; }
    private readonly Dictionary<Guid, CoreEventBucket> buckets = [];
    public ValueTask CoalesceAsync(EventBucketSource source, EventSubscriptionSnapshot subscription, CancellationToken ct = default)
    {
        lock (gate)
        {
            deliveries.TryGetValue((source.EventId, subscription.AutomationId), out var delivery);
            if (subscription.SourceKind == TriggerSourceKind.CoreEvent && delivery?.Status != EventMatchStatus.Matched) return ValueTask.CompletedTask;
            if (buckets.Values.Any(b => b.Subscription.AutomationId == subscription.AutomationId && b.Sources.Any(e => e.EventId == source.EventId))) return ValueTask.CompletedTask;
            var bucket = buckets.Values.Where(b => !b.Flushed && b.Subscription == subscription && b.DueAtUtc > source.ReceivedAtUtc && b.Sources.Count < 24 && b.Sources.Sum(e => e.DataJson.Length + 180) + source.DataJson.Length + 180 < 5500).OrderBy(b => b.DueAtUtc).FirstOrDefault();
            bucket = bucket is null ? new(CoreEventPersistence.Id($"bucket:{source.EventId:D}:{subscription.AutomationId:D}"), subscription, source.ReceivedAtUtc.AddSeconds(subscription.Dispatch.WindowSeconds!.Value), [source]) : bucket with { Sources = bucket.Sources.Append(source).ToArray() };
            if (!buckets.ContainsKey(bucket.BucketId) && buckets.Values.Count(b => !b.Flushed && b.Subscription.AutomationId == subscription.AutomationId) >= 32)
                bucket = bucket with { Flushed = true, CompletionCode = "bucket-capacity" };
            buckets[bucket.BucketId] = bucket;
            if (delivery is not null) deliveries[(source.EventId, subscription.AutomationId)] = delivery with { Status = bucket.CompletionCode is null ? EventMatchStatus.Coalesced : EventMatchStatus.BudgetSkipped, Code = bucket.CompletionCode };
        }
        return ValueTask.CompletedTask;
    }
    public ValueTask<IReadOnlyList<CoreEventBucket>> DueBucketsAsync(DateTimeOffset now, CancellationToken ct = default)
    { lock (gate) return ValueTask.FromResult<IReadOnlyList<CoreEventBucket>>(buckets.Values.Where(b => !b.Flushed && b.DueAtUtc <= now).OrderBy(b => b.DueAtUtc).Take(32).ToArray()); }
    public ValueTask CompleteBucketAsync(Guid bucketId, CancellationToken ct = default, string? code = null)
    { lock (gate) { if (buckets.TryGetValue(bucketId, out var b)) buckets[bucketId] = b with { Flushed = true, CompletionCode = code }; } return ValueTask.CompletedTask; }
    internal void Purge(Guid instanceId)
    {
        lock (gate)
        {
            foreach (var id in receipts.Where(r => r.Value.Event.Owner.AgentInstanceId == instanceId).Select(r => r.Key).ToArray()) receipts.Remove(id);
            foreach (var key in deliveries.Where(d => d.Value.Subscription.Owner.AgentInstanceId == instanceId).Select(d => d.Key).ToArray()) deliveries.Remove(key);
            foreach (var id in buckets.Where(b => b.Value.Subscription.Owner.AgentInstanceId == instanceId).Select(b => b.Key).ToArray())
            { buckets.Remove(id); inspected.RemoveWhere(i => i.BucketId == id); }
        }
    }
    internal void Append(CoreEventOccurrence? e)
    { if (e is null) return; lock (gate) receipts.TryAdd(e.EventId, new(e, e.OccurredAtUtc, false)); }
    public ValueTask<IReadOnlyList<CoreEventReceipt>> PendingAsync(CancellationToken ct = default)
    { lock (gate) return ValueTask.FromResult<IReadOnlyList<CoreEventReceipt>>(receipts.Values.Where(e => !e.Snapshotted || deliveries.Values.Any(d => d.EventId == e.Event.EventId && d.Status is EventMatchStatus.Pending or EventMatchStatus.Matched)).OrderBy(e => e.ReceivedAtUtc).ThenBy(e => e.Event.EventId).Take(32).ToArray()); }
    public ValueTask SnapshotAsync(Guid eventId, IReadOnlyList<EventSubscriptionSnapshot> subscriptions, CancellationToken ct = default)
    { lock (gate) { if (!receipts.TryGetValue(eventId, out var r) || r.Snapshotted) return ValueTask.CompletedTask;
        foreach (var s in subscriptions.Where(s => s.Owner == r.Event.Owner)) deliveries.TryAdd((eventId, s.AutomationId), new(eventId, s, EventMatchStatus.Pending));
        receipts[eventId] = r with { Snapshotted = true }; } return ValueTask.CompletedTask; }
    public ValueTask<IReadOnlyList<CoreEventDelivery>> DeliveriesAsync(Guid eventId, CancellationToken ct = default)
    { lock (gate) return ValueTask.FromResult<IReadOnlyList<CoreEventDelivery>>(deliveries.Values.Where(d => d.EventId == eventId).OrderBy(d => d.Subscription.AutomationId).ToArray()); }
    public ValueTask<EventFilterResult> DecideAsync(Guid eventId, Guid automationId, EventFilterResult decision, CancellationToken ct = default)
    {
        lock (gate)
        {
            if (!deliveries.TryGetValue((eventId, automationId), out var d)) return ValueTask.FromResult(new EventFilterResult(null, "error", "delivery-unavailable"));
            if (d.Status is EventMatchStatus.PolicySkipped or EventMatchStatus.LoopSkipped or EventMatchStatus.BudgetSkipped) return ValueTask.FromResult(new EventFilterResult(null, "error", "delivery-unavailable"));
            if (d.Decision is not null) return ValueTask.FromResult(d.Decision);
            deliveries[(eventId, automationId)] = d with { Decision = decision, Status = decision.Matched == true ? EventMatchStatus.Matched : decision.Matched == false ? EventMatchStatus.Filtered : EventMatchStatus.FilterError, Code = decision.Code };
            return ValueTask.FromResult(decision);
        }
    }
    public ValueTask FinishAsync(Guid eventId, Guid automationId, EventMatchStatus status, string? code = null, CancellationToken ct = default)
    { lock (gate) { if (deliveries.TryGetValue((eventId, automationId), out var d) && d.Status is EventMatchStatus.Pending or EventMatchStatus.Matched) deliveries[(eventId, automationId)] = d with { Status = status, Code = code }; } return ValueTask.CompletedTask; }
    public ValueTask<IReadOnlyList<CoreEventDelivery>> ActivityAsync(TriggerOwner owner, CancellationToken ct = default)
    { lock (gate) return ValueTask.FromResult<IReadOnlyList<CoreEventDelivery>>(deliveries.Values.Where(d => d.Subscription.Owner == owner).TakeLast(100).ToArray()); }
}
