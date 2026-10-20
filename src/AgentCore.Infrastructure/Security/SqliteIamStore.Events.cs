using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Events;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore : ITenantEventStore
{
    private static CoreEventReceipt CoreFact(CoreEventRecord row) => new(JsonSerializer.Deserialize<CoreEventOccurrence>(row.PayloadJson, CoreEventPersistence.Json)!,
        DateTimeOffset.FromUnixTimeMilliseconds(row.ReceivedAtUtc), row.Snapshotted);
    private static TenantEventDelivery CoreDelivery(CoreEventDeliveryRecord row, CoreEventRecord fact) => new(row.TenantId!.Value,
        JsonSerializer.Deserialize<EventSubscriptionSnapshot>(row.SnapshotJson, CoreEventPersistence.Json)!, (EventMatchStatus)row.Status,
        row.DecisionJson is { } decision ? JsonSerializer.Deserialize<EventFilterResult>(decision, CoreEventPersistence.Json) : null, CoreFact(fact), null, row.Code,
        row.ReservedOccurrenceId is { } occurrence ? Guid.Parse(occurrence) : null);
    private static TenantEventDelivery WebhookDelivery(ExternalEventDeliveryRecord row, ExternalEventRecord fact)
    {
        var decision = row.DecisionJson is { } json ? JsonSerializer.Deserialize<EventFilterResult>(json, CoreEventPersistence.Json) : null;
        var status = (ExternalEventDeliveryStatus)row.Status switch {
            ExternalEventDeliveryStatus.Pending => decision?.Matched == true ? EventMatchStatus.Matched : EventMatchStatus.Pending,
            ExternalEventDeliveryStatus.Filtered => EventMatchStatus.Filtered, ExternalEventDeliveryStatus.FilterError => EventMatchStatus.FilterError,
            ExternalEventDeliveryStatus.Coalesced => EventMatchStatus.Coalesced, ExternalEventDeliveryStatus.Admitted => EventMatchStatus.Admitted,
            ExternalEventDeliveryStatus.AwaitingAdmission => EventMatchStatus.AwaitingAdmission, _ => EventMatchStatus.PolicySkipped };
        return new(row.TenantId!.Value, JsonSerializer.Deserialize<EventSubscriptionSnapshot>(row.SnapshotJson!, CoreEventPersistence.Json)!, status, decision,
            null, SqliteExternalEventStore.ToEvent(fact), row.Code, row.ReservedOccurrenceId is { } id ? Guid.Parse(id) : null);
    }
    private static async Task<TenantEventDelivery?> TenantEvent(AgentCoreDbContext db, Guid tenant, TriggerSourceKind kind, Guid id, Guid trigger, CancellationToken ct)
    {
        var eventId = id.ToString("D"); var triggerId = trigger.ToString("D");
        if (kind == TriggerSourceKind.CoreEvent)
        {
            var row = await db.CoreEventDeliveries.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(d => d.TenantId == tenant && d.EventId == eventId && d.TriggerId == triggerId, ct);
            var fact = row is null ? null : await db.CoreEvents.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(e => e.TenantId == tenant && e.EventId == eventId && e.AgentInstanceId == row.AgentInstanceId && e.ExecutorPrincipalId == row.ExecutorPrincipalId, ct);
            return row is null || fact is null ? null : CoreDelivery(row, fact);
        }
        if (kind == TriggerSourceKind.ApplicationEvent)
        {
            var row = await db.ExternalEventDeliveries.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(d => d.TenantId == tenant && d.EventId == eventId && d.TriggerId == triggerId, ct);
            var fact = row is null ? null : await db.ExternalEvents.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(e => e.TenantId == tenant && e.EventId == eventId, ct);
            return row is null || fact is null ? null : WebhookDelivery(row, fact);
        }
        throw new ArgumentException("A concrete tenant event family is required.");
    }
    private static bool PendingTenantEvent(TenantEventDelivery value) => value.ReservedOccurrenceId is null
        && (value.Status is EventMatchStatus.Pending or EventMatchStatus.Matched || value.Status == EventMatchStatus.FilterError && value.Decision?.Retryable == true);
    private static bool SameTenantEvent(TenantEventDelivery current, TenantEventDelivery expected) => current.TenantId == expected.TenantId
        && current.EventId == expected.EventId && JsonSerializer.Serialize(current.Subscription) == JsonSerializer.Serialize(expected.Subscription);
    private async Task RequireTenantEvent(AgentCoreDbContext db, TenantEventDelivery value, CancellationToken ct)
    {
        var s = value.Subscription; var state = await ReadTenantAsync(db, value.TenantId, ct) ?? throw new AccessDeniedException("tenant_unavailable");
        var row = await OwnedAgents(db, value.TenantId).SingleOrDefaultAsync(a => a.Instance.InstanceId == s.Owner.AgentInstanceId.ToString("D"), ct)
            ?? throw new AccessDeniedException("event_executor_unavailable"); var agent = Owned(row);
        if (agent.Instance.Lifecycle != AgentCore.Domain.Definitions.AgentInstanceLifecycle.Active || agent.PrincipalId != s.Owner.ExecutorPrincipalId)
            throw new AccessDeniedException("event_executor_unavailable");
        TenantSessionRules.RequireExecutor(state, agent);
        await TenantEventAdmissionRules.RawReadAsync(state, agent.PrincipalId, s, [value.EventId], new IamAuthorizer(this), ct);
        var source = await OwnedAutomations(db, value.TenantId).AsNoTracking().SingleOrDefaultAsync(a => a.AutomationId == s.AutomationId.ToString("D"), ct)
            ?? throw new AccessDeniedException("event_subscription_changed");
        _ = TenantEventAdmissionRules.Child(TriggerStoreMapping.ToRegistration(source), s);
        var policy = InstanceSettingsResolver.Resolve(await TenantAgentDefinitionBaseline(db, agent, ct), agent.Instance.SettingsOverrides);
        if (!AgentCore.Application.Triggers.OccurrenceCompatibility.Allows(policy, s.SourceKind)) throw new AccessDeniedException("event_policy_unavailable");
        if (s.SourceKind == TriggerSourceKind.ApplicationEvent && (s.ResourceId is not { } resource || !await db.WebhookEvents.IgnoreQueryFilters().AnyAsync(w => w.TenantId == value.TenantId && w.ResourceId == resource.ToString("D") && w.Status == (int)WebhookEventStatus.Active, ct)))
            throw new AccessDeniedException("event_source_unavailable");
    }
    private static async Task PutTenantEvent(AgentCoreDbContext db, TenantEventDelivery value, CancellationToken ct)
    {
        var id = value.EventId.ToString("D"); var trigger = value.Subscription.TriggerId.ToString("D"); var json = value.Decision is null ? null : JsonSerializer.Serialize(value.Decision, CoreEventPersistence.Json);
        if (value.Subscription.SourceKind == TriggerSourceKind.CoreEvent)
        {
            var row = await db.CoreEventDeliveries.IgnoreQueryFilters().SingleAsync(d => d.TenantId == value.TenantId && d.EventId == id && d.TriggerId == trigger, ct);
            row.Status = (int)value.Status; row.DecisionJson = json; row.Code = value.Code; row.ReservedOccurrenceId = value.ReservedOccurrenceId?.ToString("D");
        }
        else
        {
            var row = await db.ExternalEventDeliveries.IgnoreQueryFilters().SingleAsync(d => d.TenantId == value.TenantId && d.EventId == id && d.TriggerId == trigger, ct);
            var status = value.Status switch { EventMatchStatus.Pending or EventMatchStatus.Matched => ExternalEventDeliveryStatus.Pending,
                EventMatchStatus.Filtered => ExternalEventDeliveryStatus.Filtered, EventMatchStatus.FilterError => ExternalEventDeliveryStatus.FilterError,
                EventMatchStatus.Coalesced => ExternalEventDeliveryStatus.Coalesced, EventMatchStatus.Admitted => ExternalEventDeliveryStatus.Admitted,
                EventMatchStatus.AwaitingAdmission => ExternalEventDeliveryStatus.AwaitingAdmission, _ => ExternalEventDeliveryStatus.Skipped };
            row.Status = (int)status; row.DecisionJson = json; row.Code = value.Code; row.ReservedOccurrenceId = value.ReservedOccurrenceId?.ToString("D");
        }
    }
    public async ValueTask<TenantEventDelivery?> FindTenantEventDeliveryAsync(Guid tenant, TriggerSourceKind kind, Guid eventId, Guid triggerId, CancellationToken ct = default)
    { await using var db = await contexts.CreateDbContextAsync(ct); return await TenantEvent(db, tenant, kind, eventId, triggerId, ct); }
    public async ValueTask<IReadOnlyList<TenantEventDelivery>> ListPendingTenantEventDeliveriesAsync(TriggerSourceKind kind, int limit, TenantEventCursor? after = null, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); var take = Math.Clamp(limit, 1, 64); var at = after?.ReceivedAt.ToUnixTimeMilliseconds(); var id = after?.EventId.ToString("D"); var trigger = after?.TriggerId.ToString("D");
        if (kind == TriggerSourceKind.CoreEvent)
        {
            var query = from d in db.CoreEventDeliveries.IgnoreQueryFilters().AsNoTracking() join e in db.CoreEvents.IgnoreQueryFilters().AsNoTracking() on d.EventId equals e.EventId
                where d.TenantId != null && d.TenantId == e.TenantId && d.AgentInstanceId == e.AgentInstanceId && d.ExecutorPrincipalId == e.ExecutorPrincipalId && d.ReservedOccurrenceId == null
                    && (d.Status == (int)EventMatchStatus.Pending || d.Status == (int)EventMatchStatus.Matched)
                    && (at == null || e.ReceivedAtUtc > at || e.ReceivedAtUtc == at && (d.EventId.CompareTo(id) > 0 || d.EventId == id && d.TriggerId.CompareTo(trigger) > 0))
                orderby e.ReceivedAtUtc, d.EventId, d.TriggerId select new { d, e };
            return (await query.Take(take).ToArrayAsync(ct)).Select(r => CoreDelivery(r.d, r.e)).ToArray();
        }
        if (kind == TriggerSourceKind.ApplicationEvent)
        {
            var query = from d in db.ExternalEventDeliveries.IgnoreQueryFilters().AsNoTracking() join e in db.ExternalEvents.IgnoreQueryFilters().AsNoTracking() on d.EventId equals e.EventId
                where d.TenantId != null && d.TenantId == e.TenantId && d.ReservedOccurrenceId == null && d.Status == (int)ExternalEventDeliveryStatus.Pending
                    && (at == null || e.AdmittedAtUtc > at || e.AdmittedAtUtc == at && (d.EventId.CompareTo(id) > 0 || d.EventId == id && d.TriggerId.CompareTo(trigger) > 0))
                orderby e.AdmittedAtUtc, d.EventId, d.TriggerId select new { d, e };
            return (await query.Take(take).ToArrayAsync(ct)).Select(r => WebhookDelivery(r.d, r.e)).ToArray();
        }
        throw new ArgumentException("A concrete tenant event family is required.");
    }
    public async ValueTask<EventFilterResult> TryDecideTenantEventAsync(TenantEventDelivery source, EventFilterResult? expected, EventFilterResult decision, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var current = await TenantEvent(db, source.TenantId, source.Subscription.SourceKind, source.EventId, source.Subscription.TriggerId, ct);
        if (current is null || !SameTenantEvent(current, source) || !PendingTenantEvent(current)) return new(null, "error", "delivery-unavailable");
        await RequireTenantEvent(db, current, ct);
        if (current.Decision is { } prior && (!prior.Retryable || prior != expected)) return prior;
        if (current.Decision != expected) return new(null, "error", "delivery-changed");
        var next = current with { Decision = decision, Status = decision.Retryable ? EventMatchStatus.Pending : decision.Matched == true ? EventMatchStatus.Matched
            : decision.Matched == false ? EventMatchStatus.Filtered : EventMatchStatus.FilterError, Code = decision.Code };
        await PutTenantEvent(db, next, ct); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return decision;
    }
    public async ValueTask<bool> TryFinishTenantEventAsync(TenantEventDelivery source, EventMatchStatus status, string code, CancellationToken ct = default)
    {
        if (status is not (EventMatchStatus.PolicySkipped or EventMatchStatus.LoopSkipped or EventMatchStatus.BudgetSkipped)) throw new ArgumentException("Execution settlement belongs to the occurrence transaction.");
        if (string.IsNullOrWhiteSpace(code) || code.Length > 128) throw new ArgumentException("A bounded factual reason is required.");
        await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var current = await TenantEvent(db, source.TenantId, source.Subscription.SourceKind, source.EventId, source.Subscription.TriggerId, ct);
        if (current is null || !SameTenantEvent(current, source) || !PendingTenantEvent(current)) return false;
        await PutTenantEvent(db, current with { Status = status, Code = code }, ct); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return true;
    }
    public async ValueTask<bool> TryCoalesceTenantEventAsync(TenantEventDelivery source, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var current = await TenantEvent(db, source.TenantId, source.Subscription.SourceKind, source.EventId, source.Subscription.TriggerId, ct);
        if (current is null || !SameTenantEvent(current, source) || !PendingTenantEvent(current) || current.Status != EventMatchStatus.Matched || current.Decision?.Matched != true) return false;
        await RequireTenantEvent(db, current, ct); var s = current.Subscription;
        if (s.Dispatch.Mode != EventDispatchMode.CoalesceLatest) throw new ArgumentException("A coalescing subscription is required.");
        var core = current.Core?.Event; var key = core?.Key;
        if (key is null) key = (await db.WebhookEvents.IgnoreQueryFilters().AsNoTracking().SingleAsync(w => w.TenantId == current.TenantId && w.ResourceId == s.ResourceId!.Value.ToString("D"), ct)).EventKey;
        var entry = TenantEventAdmissionRules.BucketSource(current, key);
        var candidates = await db.CoreEventBuckets.IgnoreQueryFilters().Where(b => b.TenantId == source.TenantId && b.TriggerId == s.TriggerId.ToString("D") && !b.Flushed && b.ReservedOccurrenceId == null).OrderBy(b => b.DueAtUtc).Take(33).ToArrayAsync(ct);
        var row = candidates.FirstOrDefault(r => r.DueAtUtc > current.ReceivedAt.ToUnixTimeMilliseconds()
            && JsonSerializer.Deserialize<CoreEventBucket>(r.PayloadJson, CoreEventPersistence.Json) is { } saved && saved.Subscription == s && EventBucketPacking.Contains(saved, entry.ReceivedAtUtc) && EventBucketPacking.CanAppend(saved.Sources, entry));
        var fresh = row is null; var bucket = fresh ? new CoreEventBucket(CoreEventPersistence.Id($"bucket:{entry.EventId:D}:{s.TriggerId:D}"), s, entry.ReceivedAtUtc.AddSeconds(s.Dispatch.WindowSeconds!.Value), [entry])
            : EventBucket(row!);
        if (!fresh) bucket = bucket with { Sources = bucket.Sources.Append(entry).ToArray() };
        if (!EventBucketPacking.CanAppend([], entry)) bucket = bucket with { Flushed = true, CompletionCode = "evidence-budget" };
        else if (fresh && candidates.Length >= 32) bucket = bucket with { Flushed = true, CompletionCode = "bucket-capacity" };
        if (fresh) { row = new() { BucketId = bucket.BucketId.ToString("D"), TenantId = current.TenantId, ExecutorPrincipalId = s.Owner.ExecutorPrincipalId,
            AgentInstanceId = s.Owner.AgentInstanceId.ToString("D"), AutomationId = s.AutomationId.ToString("D"), TriggerId = s.TriggerId.ToString("D"), TriggerRevision = s.TriggerRevision, DueAtUtc = bucket.DueAtUtc.ToUnixTimeMilliseconds() }; db.CoreEventBuckets.Add(row); }
        row!.Flushed = bucket.Flushed; row.PayloadJson = JsonSerializer.Serialize(bucket, CoreEventPersistence.Json);
        await PutTenantEvent(db, current with { Status = bucket.Flushed ? EventMatchStatus.BudgetSkipped : EventMatchStatus.Coalesced, Code = bucket.CompletionCode }, ct);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return true;
    }
    public async ValueTask<IReadOnlyList<TenantEventBucket>> ListDueTenantEventBucketsAsync(DateTimeOffset at, int limit, EventRecoveryCursor? after = null, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); var timestamp = after?.AtUtc.ToUnixTimeMilliseconds(); var id = after?.Id.ToString("D");
        var rows = await db.CoreEventBuckets.IgnoreQueryFilters().AsNoTracking().Where(b => b.TenantId != null && !b.Flushed && b.ReservedOccurrenceId == null && b.DueAtUtc <= at.ToUnixTimeMilliseconds()
            && (timestamp == null || b.DueAtUtc > timestamp || b.DueAtUtc == timestamp && b.BucketId.CompareTo(id) > 0)).OrderBy(b => b.DueAtUtc).ThenBy(b => b.BucketId).Take(Math.Clamp(limit, 1, 64)).ToArrayAsync(ct);
        return rows.Select(r => new TenantEventBucket(r.TenantId!.Value, EventBucket(r))).ToArray();
    }
    public async ValueTask<TenantEventBucket?> FindTenantEventBucketAsync(Guid tenant, Guid bucket, CancellationToken ct = default)
    { await using var db = await contexts.CreateDbContextAsync(ct); var row = await db.CoreEventBuckets.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(b => b.TenantId == tenant && b.BucketId == bucket.ToString("D"), ct); return row is null ? null : new(tenant, EventBucket(row)); }
    public async ValueTask<bool> TryFinishTenantEventBucketAsync(TenantEventBucket source, string code, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 128) throw new ArgumentException("A bounded factual reason is required.");
        await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await db.CoreEventBuckets.IgnoreQueryFilters().SingleOrDefaultAsync(b => b.TenantId == source.TenantId && b.BucketId == source.Bucket.BucketId.ToString("D"), ct);
        if (row is null || row.Flushed || row.ReservedOccurrenceId is not null || JsonSerializer.Serialize(EventBucket(row)) != JsonSerializer.Serialize(source.Bucket)) return false;
        row.Flushed = true; row.PayloadJson = JsonSerializer.Serialize(source.Bucket with { Flushed = true, CompletionCode = code }, CoreEventPersistence.Json);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return true;
    }
    public async ValueTask<CoreEventReceipt?> FindTenantCoreEventAsync(Guid tenant, Guid eventId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await db.CoreEvents.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(e => e.TenantId == tenant && e.EventId == eventId.ToString("D"), ct).ConfigureAwait(false);
        return row is null ? null : CoreFact(row);
    }
    public async ValueTask<TenantCoreEventPage> ListTenantCoreEventsAsync(Guid tenant, Guid instance, string key, int page, int pageSize, CancellationToken ct = default, IReadOnlyList<Guid>? allowedIds = null)
    {
        if (!CoreEventCatalog.Keys.Contains(key)) throw new ArgumentException("Unknown built-in event source.");
        TenantWebhookRules.Page(page, pageSize); await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var query = db.CoreEvents.IgnoreQueryFilters().AsNoTracking().Where(e => e.TenantId == tenant && e.AgentInstanceId == instance.ToString("D") && e.DedupeKey.StartsWith(key + ":"));
        if (allowedIds is not null) { var allowed = allowedIds.Select(id => id.ToString("D")).ToArray(); query = query.Where(e => allowed.Contains(e.EventId)); }
        var total = await query.CountAsync(ct); var rows = await query.OrderByDescending(e => e.ReceivedAtUtc).ThenBy(e => e.EventId).Skip(page * pageSize).Take(pageSize).ToArrayAsync(ct);
        await tx.CommitAsync(ct); return new(rows.Select(CoreFact).ToArray(), total, page, pageSize);
    }
}
