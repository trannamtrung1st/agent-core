using AgentCore.Domain.Events;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Ports;

public sealed record CoreEventReceipt(CoreEventOccurrence Event, DateTimeOffset ReceivedAtUtc, bool Snapshotted);
public sealed record CoreEventDelivery(Guid EventId, EventSubscriptionSnapshot Subscription, EventMatchStatus Status,
    EventFilterResult? Decision = null, string? Code = null);

public sealed record CoreEventBucket(Guid BucketId, EventSubscriptionSnapshot Subscription, DateTimeOffset DueAtUtc, IReadOnlyList<EventBucketSource> Sources, bool Flushed = false, string? CompletionCode = null);

public sealed record EventSourceCoverage(Guid BucketId, EventBucketSource Source, string? CompletionCode = null);
public sealed record EventCoveragePage(IReadOnlyList<EventSourceCoverage> Items, string? NextCursor);

public interface ICoreEventStore
{
    ValueTask<EventCoveragePage> CoveragePageAsync(TriggerOwner owner, Guid automationId, string? cursor, int limit, CancellationToken ct = default);
    ValueTask MarkSourceInspectedAsync(TriggerOwner owner, Guid bucketId, Guid sourceId, CancellationToken ct = default);
    ValueTask CoalesceAsync(EventBucketSource source, EventSubscriptionSnapshot subscription, CancellationToken ct = default);
    ValueTask<IReadOnlyList<CoreEventBucket>> DueBucketsAsync(DateTimeOffset now, CancellationToken ct = default);
    ValueTask CompleteBucketAsync(Guid bucketId, CancellationToken ct = default, string? code = null);
    ValueTask<IReadOnlyList<CoreEventReceipt>> PendingAsync(CancellationToken ct = default);
    ValueTask SnapshotAsync(Guid eventId, IReadOnlyList<EventSubscriptionSnapshot> subscriptions, CancellationToken ct = default);
    ValueTask<IReadOnlyList<CoreEventDelivery>> DeliveriesAsync(Guid eventId, CancellationToken ct = default);
    ValueTask DecideAsync(Guid eventId, Guid automationId, EventFilterResult decision, CancellationToken ct = default);
    ValueTask FinishAsync(Guid eventId, Guid automationId, EventMatchStatus status, string? code = null, CancellationToken ct = default);
    ValueTask<IReadOnlyList<CoreEventDelivery>> ActivityAsync(TriggerOwner owner, CancellationToken ct = default);
}
