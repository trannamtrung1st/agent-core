using AgentCore.Domain.Events;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Ports;

// Typed views of incumbent CoreEvents, ExternalEvents, delivery and bucket owners.
// No new event queue or executable resource is introduced by these projections.
public sealed record TenantEventCursor(DateTimeOffset ReceivedAt, Guid EventId, Guid TriggerId);
public sealed record TenantEventDelivery(Guid TenantId, EventSubscriptionSnapshot Subscription, EventMatchStatus Status,
    EventFilterResult? Decision, CoreEventReceipt? Core, ExternalEvent? Webhook, string? Code = null, Guid? ReservedOccurrenceId = null)
{
    public Guid EventId => Core?.Event.EventId ?? Webhook!.EventId;
    public DateTimeOffset ReceivedAt => Core?.ReceivedAtUtc ?? Webhook!.AdmittedAtUtc;
    public DateTimeOffset OccurredAt => Core?.Event.OccurredAtUtc ?? Webhook!.OccurredAtUtc;
}
public sealed record TenantEventBucket(Guid TenantId, CoreEventBucket Bucket);
public sealed record TenantEventReadVisibility(IReadOnlyList<Guid>? CoreEvents, IReadOnlyList<Guid>? Webhooks);
public sealed record TenantEventActivity(TenantEventDelivery Delivery, TenantEventBucket? Bucket, TriggerOccurrence? Occurrence, bool BatchReadable);
public sealed record TenantEventActivityPage(IReadOnlyList<TenantEventActivity> Items, int Total, int Page, int PageSize);
public sealed record TenantCoreEventPage(IReadOnlyList<CoreEventReceipt> Items, int Total, int Page, int PageSize);

// A facet of the current IAM/source transaction. Producers stage immutable facts
// and matching subscriptions with their own commit; callers cannot submit events.
public interface ITenantEventStore
{
    ValueTask<TenantEventActivityPage> ListTenantEventActivityAsync(Guid tenant, Guid instance, Guid automation,
        TenantEventReadVisibility visibility, int page, int pageSize, CancellationToken ct = default);
    ValueTask<TenantEventDelivery?> FindTenantEventDeliveryAsync(Guid tenant, TriggerSourceKind kind, Guid eventId, Guid triggerId, CancellationToken ct = default);
    ValueTask<IReadOnlyList<TenantEventDelivery>> ListPendingTenantEventDeliveriesAsync(TriggerSourceKind kind, int limit,
        TenantEventCursor? after = null, CancellationToken ct = default);
    ValueTask<EventFilterResult> TryDecideTenantEventAsync(TenantEventDelivery source, EventFilterResult? expected,
        EventFilterResult decision, CancellationToken ct = default);
    ValueTask<bool> TryFinishTenantEventAsync(TenantEventDelivery source, EventMatchStatus status, string code, CancellationToken ct = default);
    ValueTask<bool> TryCoalesceTenantEventAsync(TenantEventDelivery source, CancellationToken ct = default);
    ValueTask<IReadOnlyList<TenantEventBucket>> ListDueTenantEventBucketsAsync(DateTimeOffset at, int limit,
        EventRecoveryCursor? after = null, CancellationToken ct = default);
    ValueTask<TenantEventBucket?> FindTenantEventBucketAsync(Guid tenant, Guid bucket, CancellationToken ct = default);
    ValueTask<bool> TryFinishTenantEventBucketAsync(TenantEventBucket source, string code, CancellationToken ct = default);
    ValueTask<CoreEventReceipt?> FindTenantCoreEventAsync(Guid tenant, Guid eventId, CancellationToken ct = default);
    ValueTask<TenantCoreEventPage> ListTenantCoreEventsAsync(Guid tenant, Guid instance, string key, int page, int pageSize, CancellationToken ct = default, IReadOnlyList<Guid>? allowedIds = null);
}
