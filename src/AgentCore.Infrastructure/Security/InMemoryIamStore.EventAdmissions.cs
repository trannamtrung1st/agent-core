using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Events;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    // Used while the IAM, registration, receipt and bucket gates are held. A
    // caller-provided witness never substitutes for reconstruction from owners.
    private void RequireTenantEventAdmission(TenantAutomationAdmission admission, PreparedAutomationAdmission? consent, bool reserved, CancellationToken ct)
    {
        var source = admission.EventSource;
        if (admission.Occurrence.SourceKind is not (TriggerSourceKind.CoreEvent or TriggerSourceKind.ApplicationEvent))
        { if (source is not null) throw new ArgumentException("A non-event admission cannot claim event evidence."); return; }
        if (source is null || consent is not null && JsonSerializer.Serialize(source) != JsonSerializer.Serialize(consent.EventSource))
            throw new ArgumentException("An event admission requires its exact frozen source.");
        RequireTenantEventSource(admission.Current, consent?.Occurrence ?? TenantEventAdmissionRules.Initial(admission.Occurrence), source, reserved, ct);
    }
    private void RequireTenantEventSource(Automation current, TriggerOccurrence initial, PreparedEventAdmissionSource source, bool reserved, CancellationToken ct)
    {
        var tenant = current.Owner.TenantId!.Value;
        long? revision = source.SourceKind == TriggerSourceKind.ApplicationEvent
            ? webhookBacking.TenantSource(tenant, current.Triggers.Single(t => t.TriggerId == source.TriggerId).Source!.EventId!.Value)?.Revision : null;
        if (revision != source.ResourceRevision) throw new SecurityConflictException("event_source_changed");
        PreparedEventAdmissionSource frozen; TriggerOccurrence rebuilt;
        if (!source.IsBucket)
        {
            var delivery = TenantEvent(tenant, source.SourceKind, source.SourceId, source.TriggerId) ?? throw new AccessDeniedException("event_source_unavailable");
            if (reserved ? delivery.Status != EventMatchStatus.AwaitingAdmission || delivery.ReservedOccurrenceId != initial.OccurrenceId
                : delivery.Status != EventMatchStatus.Matched || delivery.ReservedOccurrenceId is not null) throw new SecurityConflictException("event_source_changed");
            RequireTenantEvent(delivery, ct);
            var matched = delivery with { Status = EventMatchStatus.Matched, ReservedOccurrenceId = null };
            frozen = TenantEventAdmissionRules.Freeze(matched, revision);
            rebuilt = TenantEventAdmissionRules.Occurrence(current, matched, initial.ModelPin!, initial.AdmittedAtUtc, revision);
        }
        else
        {
            var bucket = sessionBacking.CoreEvents.TenantBucket(tenant, source.SourceId) ?? throw new AccessDeniedException("event_source_unavailable");
            if (bucket.Flushed || (reserved ? bucket.ReservedOccurrenceId != initial.OccurrenceId : bucket.ReservedOccurrenceId is not null)) throw new SecurityConflictException("event_source_changed");
            foreach (var entry in bucket.Sources)
            {
                var delivery = TenantEvent(tenant, source.SourceKind, entry.EventId, source.TriggerId) ?? throw new AccessDeniedException("event_source_unavailable");
                if (delivery.Status != EventMatchStatus.Coalesced || delivery.Subscription != bucket.Subscription) throw new SecurityConflictException("event_source_changed");
                RequireTenantEvent(delivery, ct);
                var key = delivery.Core?.Event.Key ?? webhookBacking.TenantSource(tenant, bucket.Subscription.ResourceId!.Value)!.EventKey;
                if (JsonSerializer.Serialize(entry) != JsonSerializer.Serialize(TenantEventAdmissionRules.BucketSource(delivery, key))) throw new SecurityConflictException("event_source_changed");
            }
            var value = new TenantEventBucket(tenant, bucket);
            frozen = TenantEventAdmissionRules.Freeze(value, revision);
            rebuilt = TenantEventAdmissionRules.Occurrence(current, value, initial.ModelPin!, initial.AdmittedAtUtc, revision);
        }
        if (JsonSerializer.Serialize(frozen) != JsonSerializer.Serialize(source) || !TenantAutomationAdmissionApprovalRules.SameOccurrence(rebuilt, initial))
            throw new SecurityConflictException("event_source_changed");
    }
    private void SettleTenantEventSource(PreparedEventAdmissionSource? source, Guid occurrenceId, bool pending, string? refusal = null)
    {
        if (source is null) return;
        if (!source.IsBucket)
        {
            var delivery = TenantEvent(source.TenantId, source.SourceKind, source.SourceId, source.TriggerId) ?? throw new AccessDeniedException("event_source_unavailable");
            if (delivery.ReservedOccurrenceId is { } reserved && reserved != occurrenceId) throw new SecurityConflictException("event_source_changed");
            PutTenantEvent(delivery with { ReservedOccurrenceId = occurrenceId, Status = pending ? EventMatchStatus.AwaitingAdmission
                : refusal is null ? EventMatchStatus.Admitted : EventMatchStatus.PolicySkipped, Code = refusal });
        }
        else
        {
            var bucket = sessionBacking.CoreEvents.TenantBucket(source.TenantId, source.SourceId) ?? throw new AccessDeniedException("event_source_unavailable");
            if (bucket.ReservedOccurrenceId is { } reserved && reserved != occurrenceId) throw new SecurityConflictException("event_source_changed");
            sessionBacking.CoreEvents.PutTenantBucket(bucket with { ReservedOccurrenceId = occurrenceId, Flushed = !pending, CompletionCode = refusal });
        }
    }
}
