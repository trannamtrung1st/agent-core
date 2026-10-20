using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Triggers;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    public ValueTask<TenantEventActivityPage> ListTenantEventActivityAsync(Guid tenant, Guid instance, Guid automation,
        TenantEventReadVisibility visibility, int page, int pageSize, CancellationToken ct = default)
    {
        TenantWebhookRules.Page(page, pageSize); ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(WithTenantEvents(() => {
            var core = sessionBacking.CoreEvents.TenantDeliveries().Where(d => d.Subscription.Owner.TenantId == tenant
                && d.Subscription.Owner.AgentInstanceId == instance && d.Subscription.AutomationId == automation
                && (visibility.CoreEvents is null || visibility.CoreEvents.Contains(d.EventId)))
                .Select(d => TenantEvent(tenant, TriggerSourceKind.CoreEvent, d.EventId, d.Subscription.TriggerId));
            var external = webhookBacking.TenantDeliveries().Where(d => d.Snapshot?.Owner.TenantId == tenant
                && d.Snapshot.Owner.AgentInstanceId == instance && d.AutomationId == automation
                && (visibility.Webhooks is null || visibility.Webhooks.Contains(d.Snapshot.ResourceId!.Value)))
                .Select(d => TenantEvent(tenant, TriggerSourceKind.ApplicationEvent, d.EventId, d.TriggerId));
            var rows = core.Concat(external).OfType<TenantEventDelivery>().OrderByDescending(d => d.ReceivedAt)
                .ThenBy(d => d.EventId.ToString("D"), StringComparer.Ordinal).ThenBy(d => d.Subscription.TriggerId.ToString("D"), StringComparer.Ordinal).ToArray();
            var items = rows.Skip(page * pageSize).Take(pageSize).Select(delivery => {
                var bucket = sessionBacking.CoreEvents.TenantBuckets().SingleOrDefault(b => b.Subscription.Owner.TenantId == tenant
                    && b.Subscription.AutomationId == automation && b.Subscription.TriggerId == delivery.Subscription.TriggerId
                    && b.Sources.Any(s => s.EventId == delivery.EventId));
                var readable = bucket is null || bucket.Subscription.SourceKind == TriggerSourceKind.ApplicationEvent
                    ? bucket is null || visibility.Webhooks is null || visibility.Webhooks.Contains(bucket.Subscription.ResourceId!.Value)
                    : visibility.CoreEvents is null || bucket.Sources.All(s => visibility.CoreEvents.Contains(s.EventId));
                var occurrenceId = bucket?.ReservedOccurrenceId ?? delivery.ReservedOccurrenceId;
                var occurrence = readable && occurrenceId is { } id ? automationBacking.ReadTenantOccurrence(tenant, id) : null;
                if (occurrence is not null && (occurrence.Owner != delivery.Subscription.Owner || occurrence.AutomationId != automation
                    || occurrence.TriggerId != delivery.Subscription.TriggerId)) throw new SecurityConflictException("event_activity_changed");
                return new TenantEventActivity(delivery, readable && bucket is not null ? new(tenant, bucket) : null, occurrence, readable);
            }).ToArray();
            return new TenantEventActivityPage(items, rows.Length, page, pageSize);
        }));
    }
}
