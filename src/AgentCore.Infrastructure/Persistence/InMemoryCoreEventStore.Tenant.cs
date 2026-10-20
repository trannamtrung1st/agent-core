using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Events;
using AgentCore.Domain.Triggers;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class InMemoryCoreEventStore
{
    internal Lock TenantGate => gate;
    internal bool OwnsTenantEvent(Guid tenant, Guid eventId) { lock (gate) return TenantReceipt(tenant, eventId) is not null; }
    internal CoreEventReceipt? TenantReceipt(Guid tenant, Guid eventId) => receipts.GetValueOrDefault(eventId) is { } row && row.Event.Owner.TenantId == tenant ? row : null;
    internal CoreEventDelivery? TenantDelivery(Guid tenant, Guid eventId, Guid trigger) => deliveries.GetValueOrDefault((eventId, trigger)) is { } row && row.Subscription.Owner.TenantId == tenant ? row : null;
    internal IReadOnlyList<CoreEventDelivery> TenantDeliveries() => deliveries.Values.Where(d => d.Subscription.Owner.TenantId is not null).ToArray();
    internal IReadOnlyList<CoreEventReceipt> TenantReceipts(Guid tenant, Guid instance, string key) => receipts.Values.Where(r => r.Event.Owner.TenantId == tenant && r.Event.Owner.AgentInstanceId == instance && r.Event.Key == key).ToArray();
    internal CoreEventBucket? TenantBucket(Guid tenant, Guid id) => buckets.GetValueOrDefault(id) is { } row && row.Subscription.Owner.TenantId == tenant ? row : null;
    internal IReadOnlyList<CoreEventBucket> TenantBuckets() => buckets.Values.Where(b => b.Subscription.Owner.TenantId is not null).ToArray();
    internal void PutTenantDelivery(CoreEventDelivery delivery) => deliveries[(delivery.EventId, delivery.Subscription.TriggerId)] = delivery;
    internal void PutTenantBucket(CoreEventBucket bucket) => buckets[bucket.BucketId] = bucket with { Sources = Array.AsReadOnly(bucket.Sources.ToArray()) };
    // Caller holds the source/IAM/Automation locks and derives subscriptions from
    // the actual committed owner. Replays never attach later subscriptions.
    internal CoreEventOccurrence ValidateTenantAppend(CoreEventOccurrence e, IReadOnlyList<EventSubscriptionSnapshot> subscriptions)
    {
        if (e.Owner.TenantId is null || e.Owner.ExecutorPrincipalId is null || e.TriggerDepth is < 0 or > 4
            || !CoreEventCatalog.Keys.Contains(e.Key) || subscriptions.Any(s => s.Owner != e.Owner || s.SourceKind != TriggerSourceKind.CoreEvent))
            throw new ArgumentException("A tenant Core fact requires its exact committed source owner.");
        e = e with { OccurredAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(e.OccurredAtUtc.ToUnixTimeMilliseconds()), VisitedAutomationIds = Array.AsReadOnly((e.VisitedAutomationIds ?? []).ToArray()) };
        if (receipts.TryGetValue(e.EventId, out var prior))
        {
            if (JsonSerializer.Serialize(prior.Event) != JsonSerializer.Serialize(e)) throw new SecurityConflictException("event_identity_conflict");
            return e;
        }
        if (receipts.Values.Any(r => r.Event.DedupeKey == e.DedupeKey)) throw new SecurityConflictException("event_identity_conflict");
        if (subscriptions.Select(s => s.TriggerId).Distinct().Count() != subscriptions.Count) throw new ArgumentException("Subscription identities must be distinct.");
        return e;
    }
    internal void AppendTenant(CoreEventOccurrence e, IReadOnlyList<EventSubscriptionSnapshot> subscriptions)
    {
        e = ValidateTenantAppend(e, subscriptions);
        if (receipts.ContainsKey(e.EventId)) return;
        var pending = subscriptions.Select(s => new CoreEventDelivery(e.EventId, s, EventMatchStatus.Pending)).ToArray();
        receipts.Add(e.EventId, new(e, AgentCore.Application.Triggers.TriggerScheduleCalculator.Truncate(e.OccurredAtUtc), true));
        foreach (var row in pending) deliveries.Add((e.EventId, row.Subscription.TriggerId), row);
    }
}
