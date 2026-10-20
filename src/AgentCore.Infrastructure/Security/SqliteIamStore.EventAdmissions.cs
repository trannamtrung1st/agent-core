using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Events;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    private static CoreEventBucket EventBucket(CoreEventBucketRecord row)
    {
        var value = JsonSerializer.Deserialize<CoreEventBucket>(row.PayloadJson, CoreEventPersistence.Json) ?? throw new SecurityConflictException("event_source_changed");
        if (value.ReservedOccurrenceId?.ToString("D") != row.ReservedOccurrenceId || value.Flushed != row.Flushed
            || value.Subscription.Owner.TenantId != row.TenantId || value.Subscription.Owner.ExecutorPrincipalId != row.ExecutorPrincipalId
            || value.Subscription.Owner.AgentInstanceId.ToString("D") != row.AgentInstanceId || value.BucketId.ToString("D") != row.BucketId)
            throw new SecurityConflictException("event_source_changed");
        return value;
    }
    private async Task RequireTenantEventAdmission(AgentCoreDbContext db, TenantAutomationAdmission admission, PreparedAutomationAdmission? consent, bool reserved, CancellationToken ct)
    {
        var source = admission.EventSource;
        if (admission.Occurrence.SourceKind is not (TriggerSourceKind.CoreEvent or TriggerSourceKind.ApplicationEvent))
        { if (source is not null) throw new ArgumentException("A non-event admission cannot claim event evidence."); return; }
        if (source is null || consent is not null && JsonSerializer.Serialize(source) != JsonSerializer.Serialize(consent.EventSource)) throw new ArgumentException("An event admission requires its exact frozen source.");
        await RequireTenantEventSource(db, admission.Current, consent?.Occurrence ?? TenantEventAdmissionRules.Initial(admission.Occurrence), source, reserved, ct);
    }
    private async Task RequireTenantEventSource(AgentCoreDbContext db, Automation current, TriggerOccurrence initial, PreparedEventAdmissionSource source, bool reserved, CancellationToken ct)
    {
        var tenant = current.Owner.TenantId!.Value;
        long? revision = null;
        if (source.SourceKind == TriggerSourceKind.ApplicationEvent)
        {
            var resource = ((EventTrigger)current.Triggers.Single(t => t.TriggerId == source.TriggerId).Configuration).EventId.ToString("D");
            revision = (await db.WebhookEvents.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(w => w.TenantId == tenant && w.ResourceId == resource, ct))?.Revision;
        }
        if (revision != source.ResourceRevision) throw new SecurityConflictException("event_source_changed");
        PreparedEventAdmissionSource frozen; TriggerOccurrence rebuilt;
        if (!source.IsBucket)
        {
            var delivery = await TenantEvent(db, tenant, source.SourceKind, source.SourceId, source.TriggerId, ct) ?? throw new AccessDeniedException("event_source_unavailable");
            if (reserved ? delivery.Status != EventMatchStatus.AwaitingAdmission || delivery.ReservedOccurrenceId != initial.OccurrenceId
                : delivery.Status != EventMatchStatus.Matched || delivery.ReservedOccurrenceId is not null) throw new SecurityConflictException("event_source_changed");
            await RequireTenantEvent(db, delivery, ct);
            var matched = delivery with { Status = EventMatchStatus.Matched, ReservedOccurrenceId = null };
            frozen = TenantEventAdmissionRules.Freeze(matched, revision);
            rebuilt = TenantEventAdmissionRules.Occurrence(current, matched, initial.ModelPin!, initial.AdmittedAtUtc, revision);
        }
        else
        {
            var row = await db.CoreEventBuckets.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(b => b.TenantId == tenant && b.BucketId == source.SourceId.ToString("D"), ct)
                ?? throw new AccessDeniedException("event_source_unavailable"); var bucket = EventBucket(row);
            if (bucket.Flushed || (reserved ? bucket.ReservedOccurrenceId != initial.OccurrenceId : bucket.ReservedOccurrenceId is not null)) throw new SecurityConflictException("event_source_changed");
            var email = TenantEventAdmissionRules.EmailBucket(bucket); var eligible = new List<Guid>();
            foreach (var entry in bucket.Sources)
            {
                var delivery = await TenantEvent(db, tenant, source.SourceKind, entry.EventId, source.TriggerId, ct) ?? throw new AccessDeniedException("event_source_unavailable");
                if (delivery.Status != EventMatchStatus.Coalesced || delivery.Subscription != bucket.Subscription) throw new SecurityConflictException("event_source_changed");
                var key = delivery.Core?.Event.Key ?? (await db.WebhookEvents.IgnoreQueryFilters().AsNoTracking().SingleAsync(w => w.TenantId == tenant && w.ResourceId == bucket.Subscription.ResourceId!.Value.ToString("D"), ct)).EventKey;
                if (JsonSerializer.Serialize(entry) != JsonSerializer.Serialize(TenantEventAdmissionRules.BucketSource(delivery, key))) throw new SecurityConflictException("event_source_changed");
                if (reserved && email && !source.EventIds.Contains(entry.EventId)) continue;
                try { await RequireTenantEvent(db, delivery, ct); eligible.Add(entry.EventId); }
                catch (AccessDeniedException) when (email && !reserved) { }
            }
            var value = new TenantEventBucket(tenant, bucket);
            if (email) value = TenantEventAdmissionRules.Retain(value, eligible);
            frozen = TenantEventAdmissionRules.Freeze(value, revision);
            rebuilt = TenantEventAdmissionRules.Occurrence(current, value, initial.ModelPin!, initial.AdmittedAtUtc, revision);
        }
        if (JsonSerializer.Serialize(frozen) != JsonSerializer.Serialize(source) || !TenantAutomationAdmissionApprovalRules.SameOccurrence(rebuilt, initial)) throw new SecurityConflictException("event_source_changed");
    }
    private static async Task SettleTenantEventSource(AgentCoreDbContext db, PreparedEventAdmissionSource? source, Guid occurrenceId, bool pending, string? refusal, CancellationToken ct)
    {
        if (source is null) return;
        if (!source.IsBucket)
        {
            var delivery = await TenantEvent(db, source.TenantId, source.SourceKind, source.SourceId, source.TriggerId, ct) ?? throw new AccessDeniedException("event_source_unavailable");
            if (delivery.ReservedOccurrenceId is { } reserved && reserved != occurrenceId) throw new SecurityConflictException("event_source_changed");
            await PutTenantEvent(db, delivery with { ReservedOccurrenceId = occurrenceId, Status = pending ? EventMatchStatus.AwaitingAdmission
                : refusal is null ? EventMatchStatus.Admitted : EventMatchStatus.PolicySkipped, Code = refusal }, ct);
        }
        else
        {
            var row = await db.CoreEventBuckets.IgnoreQueryFilters().SingleOrDefaultAsync(b => b.TenantId == source.TenantId && b.BucketId == source.SourceId.ToString("D"), ct)
                ?? throw new AccessDeniedException("event_source_unavailable"); var bucket = EventBucket(row);
            if (bucket.ReservedOccurrenceId is { } reserved && reserved != occurrenceId) throw new SecurityConflictException("event_source_changed");
            row.ReservedOccurrenceId = occurrenceId.ToString("D"); row.Flushed = !pending;
            row.PayloadJson = JsonSerializer.Serialize(bucket with { ReservedOccurrenceId = occurrenceId, Flushed = !pending, CompletionCode = refusal }, CoreEventPersistence.Json);
        }
    }
}
