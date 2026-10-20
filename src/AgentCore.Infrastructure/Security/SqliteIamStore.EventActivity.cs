using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    public async ValueTask<TenantEventActivityPage> ListTenantEventActivityAsync(Guid tenant, Guid instance, Guid automation,
        TenantEventReadVisibility visibility, int page, int pageSize, CancellationToken ct = default)
    {
        TenantWebhookRules.Page(page, pageSize);
        await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var core = db.CoreEventDeliveries.IgnoreQueryFilters().AsNoTracking().Where(d => d.TenantId == tenant
            && d.AgentInstanceId == instance.ToString("D") && d.AutomationId == automation.ToString("D"));
        if (visibility.CoreEvents is { } coreIds) { var ids = coreIds.Select(id => id.ToString("D")).ToArray(); core = core.Where(d => ids.Contains(d.EventId)); }
        var external = db.ExternalEventDeliveries.IgnoreQueryFilters().AsNoTracking().Where(d => d.TenantId == tenant
            && d.AgentInstanceId == instance.ToString("D") && d.AutomationId == automation.ToString("D"));
        var externalFacts = db.ExternalEvents.IgnoreQueryFilters().AsNoTracking().Where(e => e.TenantId == tenant);
        if (visibility.Webhooks is { } sourceIds) { var ids = sourceIds.Select(id => id.ToString("D")).ToArray(); externalFacts = externalFacts.Where(e => ids.Contains(e.ResourceId)); }
        var coreRows = core.Join(db.CoreEvents.IgnoreQueryFilters().Where(f => f.TenantId == tenant), d => d.EventId, f => f.EventId,
            (d, f) => new { d.EventId, d.TriggerId, At = f.ReceivedAtUtc, Kind = (int)TriggerSourceKind.CoreEvent });
        var externalRows = external.Join(externalFacts, d => d.EventId, f => f.EventId,
            (d, f) => new { d.EventId, d.TriggerId, At = f.AdmittedAtUtc, Kind = (int)TriggerSourceKind.ApplicationEvent });
        var query = coreRows.Concat(externalRows); var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(r => r.At).ThenBy(r => r.EventId).ThenBy(r => r.TriggerId).Skip(page * pageSize).Take(pageSize).ToArrayAsync(ct);
        var items = new List<TenantEventActivity>();
        foreach (var row in rows)
        {
            var delivery = await TenantEvent(db, tenant, (TriggerSourceKind)row.Kind, Guid.Parse(row.EventId), Guid.Parse(row.TriggerId), ct)
                ?? throw new SecurityConflictException("event_activity_changed");
            var buckets = await db.CoreEventBuckets.FromSqlInterpolated($"""
                SELECT * FROM CoreEventBuckets b WHERE b.TenantId = {tenant} AND b.AutomationId = {automation.ToString("D")}
                AND b.TriggerId = {row.TriggerId} AND EXISTS (SELECT 1 FROM json_each(b.PayloadJson, '$.Sources') source
                    WHERE json_extract(source.value, '$.EventId') = {row.EventId})
                """).IgnoreQueryFilters().AsNoTracking().ToArrayAsync(ct);
            var bucket = buckets.Length == 0 ? null : buckets.Length == 1 ? EventBucket(buckets[0]) : throw new SecurityConflictException("event_activity_changed");
            var readable = bucket is null || bucket.Subscription.SourceKind == TriggerSourceKind.ApplicationEvent
                ? bucket is null || visibility.Webhooks is null || visibility.Webhooks.Contains(bucket.Subscription.ResourceId!.Value)
                : visibility.CoreEvents is null || bucket.Sources.All(s => visibility.CoreEvents.Contains(s.EventId));
            var id = bucket?.ReservedOccurrenceId ?? delivery.ReservedOccurrenceId;
            var occurrenceRow = readable && id is not null ? await db.TriggerOccurrences.IgnoreQueryFilters().AsNoTracking()
                .SingleOrDefaultAsync(o => o.TenantId == tenant && o.OccurrenceId == id.Value.ToString("D"), ct) : null;
            var occurrence = occurrenceRow is null ? null : TriggerStoreMapping.ToOccurrence(occurrenceRow);
            if (occurrence is not null && (occurrence.Owner != delivery.Subscription.Owner || occurrence.AutomationId != automation
                || occurrence.TriggerId != delivery.Subscription.TriggerId)) throw new SecurityConflictException("event_activity_changed");
            items.Add(new(delivery, readable && bucket is not null ? new(tenant, bucket) : null, occurrence, readable));
        }
        await tx.CommitAsync(ct); return new(items, total, page, pageSize);
    }
}
