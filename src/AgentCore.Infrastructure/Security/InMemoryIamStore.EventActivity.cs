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
            var rows = core.Concat(external).OfType<TenantEventDelivery>()
                .Where(d => d.Core?.Event.Key != "email.message.received" || CredentialBacking().ReadTenantCredential(tenant, AgentCore.Domain.Email.EmailEventReferences.AccountId(d.Core.Event.DataJson)) is { Value.Purpose: AgentCore.Domain.Credentials.CredentialPurpose.EmailAccount }
                    && (visibility.EmailAccounts is null || visibility.EmailAccounts.Contains(AgentCore.Domain.Email.EmailEventReferences.AccountId(d.Core.Event.DataJson)))).OrderByDescending(d => d.ReceivedAt)
                .ThenBy(d => d.EventId.ToString("D"), StringComparer.Ordinal).ThenBy(d => d.Subscription.TriggerId.ToString("D"), StringComparer.Ordinal).ToArray();
            var items = rows.Skip(page * pageSize).Take(pageSize).Select(delivery => {
                var bucket = sessionBacking.CoreEvents.TenantBuckets().SingleOrDefault(b => b.Subscription.Owner.TenantId == tenant
                    && b.Subscription.AutomationId == automation && b.Subscription.TriggerId == delivery.Subscription.TriggerId
                    && b.Sources.Any(s => s.EventId == delivery.EventId));
                var occurrenceId = bucket?.ReservedOccurrenceId ?? delivery.ReservedOccurrenceId;
                var retained = occurrenceId is { } reserved ? automationBacking.ReadTenantOccurrence(tenant, reserved) : null;
                if (bucket is not null && retained is not null && TenantEventAdmissionRules.EmailBucket(bucket))
                {
                    bucket = TenantEventAdmissionRules.RetainedActivity(new(tenant, bucket), retained).Bucket;
                    if (!bucket.Sources.Any(s => s.EventId == delivery.EventId))
                        return new TenantEventActivity(delivery with { Status = AgentCore.Domain.Events.EventMatchStatus.PolicySkipped, Code = "email_event_source_unavailable" }, null, null, false);
                }
                var readable = bucket is null || bucket.Subscription.SourceKind == TriggerSourceKind.ApplicationEvent
                    ? bucket is null || visibility.Webhooks is null || visibility.Webhooks.Contains(bucket.Subscription.ResourceId!.Value)
                    : visibility.CoreEvents is null || bucket.Sources.All(s => visibility.CoreEvents.Contains(s.EventId));
                readable &= bucket is null || bucket.Sources.All(s => s.Key != "email.message.received"
                    || CredentialBacking().ReadTenantCredential(tenant, AgentCore.Domain.Email.EmailEventReferences.AccountId(s.DataJson)) is { Value.Purpose: AgentCore.Domain.Credentials.CredentialPurpose.EmailAccount }
                    && (visibility.EmailAccounts is null || visibility.EmailAccounts.Contains(AgentCore.Domain.Email.EmailEventReferences.AccountId(s.DataJson))));
                var occurrence = readable ? retained : null;
                if (occurrence is not null && (occurrence.Owner != delivery.Subscription.Owner || occurrence.AutomationId != automation
                    || occurrence.TriggerId != delivery.Subscription.TriggerId)) throw new SecurityConflictException("event_activity_changed");
                return new TenantEventActivity(delivery, readable && bucket is not null ? new(tenant, bucket) : null, occurrence, readable);
            }).ToArray();
            return new TenantEventActivityPage(items, rows.Length, page, pageSize);
        }));
    }
}
